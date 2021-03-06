using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using NGate.Framework;
using NGate.Middleware;
using Polly;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NGate
{
    public static class NGateExtensions
    {
        public static IWebHostBuilder UseNGate(this IWebHostBuilder webHostBuilder, string[] args = null)
        {
            var configPath = args != null && args.Any() ? args[0] : string.Empty;
            var configPathVariable = Environment.GetEnvironmentVariable("NGATE_CONFIG");
            if (!string.IsNullOrWhiteSpace(configPathVariable))
            {
                configPath = configPathVariable;
            }

            if (string.IsNullOrWhiteSpace(configPath))
            {
                configPath = "ngate.yml";
            }

            if (!configPath.EndsWith(".yml"))
            {
                configPath = $"{configPath}.yml";
            }

            if (!File.Exists(configPath))
            {
                throw new ArgumentException($"NGate config was not found under: '{configPath}'", nameof(configPath));
            }

            var text = File.ReadAllText(configPath);
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .WithNamingConvention(new UnderscoredNamingConvention())
                .Build();
            var configuration = deserializer.Deserialize<Configuration>(text);
            var authenticationConfig = configuration.Auth;
            var useJwt = authenticationConfig?.Type?.ToLowerInvariant() == "jwt";
            var useForwardedHeaders = configuration.UseForwardedHeaders;
            var cors = configuration?.Cors;
            var useCors = cors?.Enabled == true;
            var useErrorHandler = configuration.UseErrorHandler == true;
            var http = configuration.Http ?? new Http();
            if (configuration.SettingsPath == null)
            {
                configuration.SettingsPath = "Settings";
            }

            if (configuration.SettingsPath.EndsWith("/"))
            {
                configuration.SettingsPath = configuration.SettingsPath
                    .Substring(0, configuration.SettingsPath.Length - 1);
            }

            if (configuration.PayloadsFolder == null)
            {
                configuration.PayloadsFolder = "Payloads";
            }

            if (configuration.PayloadsFolder.EndsWith("/"))
            {
                configuration.PayloadsFolder = configuration.PayloadsFolder
                    .Substring(0, configuration.PayloadsFolder.Length - 1);
            }

            var modules = new HashSet<Module>();
            var modulesPath = string.IsNullOrWhiteSpace(configuration.ModulesPath)
                ? "Modules"
                : configuration.ModulesPath;
            if (modulesPath.EndsWith("/"))
            {
                modulesPath = modulesPath.Substring(0, modulesPath.Length - 1);
            }

            if (Directory.Exists(modulesPath))
            {
                var modulesPaths = Directory.EnumerateDirectories(modulesPath).ToList();
                foreach (var modulePath in modulesPaths)
                {
                    var fullModulePath = $"{modulePath}/module.yml";
                    if (!File.Exists(fullModulePath))
                    {
                        continue;
                    }

                    var module = deserializer.Deserialize<Module>(File.ReadAllText(fullModulePath));
                    modules.Add(module);
                }

                var allModules = new List<Module>();
                allModules.AddRange(configuration.Modules);
                allModules.AddRange(modules);
                configuration.Modules = allModules;
            }

            return webHostBuilder.ConfigureServices(s =>
                {
                    s.AddMvcCore()
                        .AddJsonFormatters()
                        .AddJsonOptions(o => o.SerializerSettings.Formatting = Formatting.Indented);

                    var httpClientBuilder = s.AddHttpClient("ngate");
                    httpClientBuilder.AddTransientHttpErrorPolicy(p =>
                        p.WaitAndRetryAsync(http.Retries, retryAttempt =>
                        {
                            var interval = http.Exponential
                                ? Math.Pow(http.Interval, retryAttempt)
                                : http.Interval;

                            return TimeSpan.FromSeconds(interval);
                        }));


                    s.AddLogging();
                    if (authenticationConfig == null || !useJwt)
                    {
                        return;
                    }

                    if (useErrorHandler)
                    {
                        s.AddTransient<ErrorHandlerMiddleware>();
                    }

                    if (useCors)
                    {
                        s.AddCors(options =>
                        {
                            var headers = cors?.Headers ?? Enumerable.Empty<string>();
                            options.AddPolicy("CorsPolicy", builder =>
                                builder.AllowAnyOrigin()
                                    .AllowAnyMethod()
                                    .AllowAnyHeader()
                                    .AllowCredentials()
                                    .WithExposedHeaders(headers.ToArray()));
                        });
                    }

                    var jwtConfig = authenticationConfig.Jwt;
                    s.AddAuthorization();
                    s.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddJwtBearer(cfg =>
                        {
                            cfg.TokenValidationParameters = new TokenValidationParameters
                            {
                                IssuerSigningKey = new SymmetricSecurityKey(Encoding
                                    .UTF8.GetBytes(jwtConfig.Key)),
                                ValidIssuer = jwtConfig.Issuer,
                                ValidIssuers = jwtConfig.Issuers,
                                ValidAudience = jwtConfig.Audience,
                                ValidAudiences = jwtConfig.Audiences,
                                ValidateIssuer = jwtConfig.ValidateIssuer,
                                ValidateAudience = jwtConfig.ValidateAudience,
                                ValidateLifetime = jwtConfig.ValidateLifetime
                            };
                        });
                })
                .Configure(app =>
                {
                    if (useErrorHandler)
                    {
                        app.UseMiddleware<ErrorHandlerMiddleware>();
                    }

                    if (useCors)
                    {
                        app.UseCors("CorsPolicy");
                    }

                    if (useJwt)
                    {
                        app.UseAuthentication();
                    }

                    if (useForwardedHeaders)
                    {
                        app.UseForwardedHeaders(new ForwardedHeadersOptions
                        {
                            ForwardedHeaders = ForwardedHeaders.All
                        });
                    }

                    foreach (var route in configuration.Modules.SelectMany(m => m.Routes))
                    {
                        route.Method =
                            (string.IsNullOrWhiteSpace(route.Method) ? "get" : route.Method).ToLowerInvariant();
                        route.DownstreamMethod =
                            (string.IsNullOrWhiteSpace(route.DownstreamMethod) ? route.Method : route.DownstreamMethod)
                            .ToLowerInvariant();
                    }

                    var routeProvider = new RouteProvider(app.ApplicationServices,
                        new RequestProcessor(configuration, new ValueProvider(), new SchemaValidator()),
                        new RouteConfigurator(configuration), new AccessValidator(configuration),
                        configuration);

                    app.UseRouter(routeProvider.Build());
                });
        }
    }
}