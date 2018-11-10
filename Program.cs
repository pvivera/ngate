﻿using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using NGate.Framework;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NGate
{
    public class Program
    {
        public static async Task Main(string[] args)
            => await CreateWebHostBuilder(args).Build().RunAsync();

        private static IWebHostBuilder CreateWebHostBuilder(string[] args)
        {
            var text = File.ReadAllText("config.yml");
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .WithNamingConvention(new UnderscoredNamingConvention())
                .Build();
            var configuration = deserializer.Deserialize<Configuration>(text);
            var authenticationConfig = configuration.Config.Authentication;
            var useJwt = authenticationConfig?.Type?.ToLowerInvariant() == "jwt";

            return WebHost.CreateDefaultBuilder(args)
                .ConfigureServices(s =>
                {
                    s.AddMvcCore()
                        .AddJsonFormatters()
                        .AddJsonOptions(o => o.SerializerSettings.Formatting = Formatting.Indented);
                    s.AddHttpClient();
                    if (authenticationConfig == null || !useJwt)
                    {
                        return;
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
                    if (useJwt)
                    {
                        app.UseAuthentication();
                    }

                    var routeProvider = new RouteProvider(app.ApplicationServices,
                        new RequestProcessor(configuration, new ValueProvider()),
                        new RouteConfigurator(configuration), configuration);
                    app.UseRouter(routeProvider.Build());
                });
        }
    }
}
