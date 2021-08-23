using System;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Writers;
using Swashbuckle.AspNetCore.Swagger;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Cli.Settings;
using System.Text.Json;
using FluentValidation;
using Swashbuckle.AspNetCore.Cli.Validators;

namespace Swashbuckle.AspNetCore.Cli
{
    public class Program
    {
        static int Main(string[] args)
        {
            // Helper to simplify command line parsing etc.
            var runner = new CommandRunner("dotnet swagger", "Swashbuckle (Swagger) Command Line Tools", Console.Out);

            // NOTE: The "dotnet swagger tofile" command does not serve the request directly. Instead, it invokes a corresponding
            // command (called _tofile) via "dotnet exec" so that the runtime configuration (*.runtimeconfig & *.deps.json) of the
            // provided startupassembly can be used instead of the tool's. This is neccessary to successfully load the
            // startupassembly and it's transitive dependencies. See https://github.com/dotnet/coreclr/issues/13277 for more.

            // > dotnet swagger tofile ...
            runner.SubCommand("tofile", "retrieves Swagger from a startup assembly, and writes to file ", c =>
            {
                c.Argument("configurationfile", "Configuration file for the settings");
                c.OnRun((namedArgs) =>
                {
                    if (!File.Exists(namedArgs["configurationfile"]))
                    {
                        throw new FileNotFoundException(namedArgs["configurationfile"]);
                    }

                    var json = File.ReadAllText(namedArgs["configurationfile"]);
                    var configurationSettings = JsonSerializer.Deserialize<ConfigurationSettings>(json);

                    var validator = new ConfigurationSettingsValidator();
                    validator.ValidateAndThrow(configurationSettings);

                    if (!File.Exists(configurationSettings.Assembly))
                    {
                        throw new FileNotFoundException(configurationSettings.Assembly);
                    }

                    var commandName = args[0];

                    var subProcessArguments = new string[args.Length - 1];
                    if (subProcessArguments.Length > 0)
                    {
                        Array.Copy(args, 1, subProcessArguments, 0, subProcessArguments.Length);
                    }

                    var subProcessCommandLine = string.Format(
                        "exec --depsfile {0} --runtimeconfig {1} {2} _{3} {4}", // note the underscore prepended to the command name
                        EscapePath(configurationSettings.DepsFile),
                        EscapePath(configurationSettings.RuntimeConfig),
                        EscapePath(typeof(Program).GetTypeInfo().Assembly.Location),
                        commandName,
                        EscapePath(args[1])
                    );

                    var subProcess = Process.Start("dotnet", subProcessCommandLine);

                    if (subProcess == null)
                    {
                        throw new Exception("call to dotnet returned null");
                    }

                    subProcess.WaitForExit();
                    return subProcess.ExitCode;
                });
            });

            // > dotnet swagger _tofile ... (* should only be invoked via "dotnet exec")
            runner.SubCommand("_tofile", "", c =>
            {
                c.Argument("configurationfile", "");
                c.OnRun((namedArgs) =>
                {
                    // 1) Bind configuration settings
                    var json = File.ReadAllText(namedArgs["configurationfile"]);
                    var configurationSettings = JsonSerializer.Deserialize<ConfigurationSettings>(json);

                    // 2) Configure host with provided startupassembly
                    var startupAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(configurationSettings.Assembly);

                    // 3) Build a service container that's based on the startup assembly
                    var serviceProvider = GetServiceProvider(startupAssembly);

                    // 4) Populate a list of versions
                    var swaggerdocs = new List<string>();

                    if (!configurationSettings.LoopThroughVersions)
                    {
                        Console.WriteLine($"Version defined: {configurationSettings.SwaggerDoc}");
                        swaggerdocs.Add(configurationSettings.SwaggerDoc);
                    }
                    else
                    {
                        Console.WriteLine("Version not defined, extract from IApiVersionDescriptionProvider");
                        var provider = serviceProvider.GetRequiredService<IApiVersionDescriptionProvider>();
                        swaggerdocs.AddRange(provider.ApiVersionDescriptions.Select(item => item.GroupName));
                    }

                    // 5) Retrieve Swagger via configured provider
                    foreach (var swaggerdoc in swaggerdocs)
                    {
                        var swaggerProvider = serviceProvider.GetRequiredService<ISwaggerProvider>();
                        var swagger = swaggerProvider.GetSwagger(
                            swaggerdoc,
                            configurationSettings.HasHost ? configurationSettings.Host : null,
                            configurationSettings.HasBasePath ? configurationSettings.BasePath : null);

                        // 6) Serialize to specified output location or stdout
                        string outputPath = null;

                        if (configurationSettings.HasOutput && !Directory.Exists(configurationSettings.Output))
                        {
                            throw new DirectoryNotFoundException($"{configurationSettings.Output} does not exist");
                        }

                        if (configurationSettings.HasOutput)
                        {
                            outputPath = Path.Join(configurationSettings.Output);
                            Console.WriteLine($"Path: {outputPath}");
                        }

                        if (configurationSettings.OutputYaml)
                        {
                            Output<OpenApiYamlWriter>(outputPath, configurationSettings.SerializeAsV2, swagger, swaggerdoc, "yaml");
                        }

                        if (configurationSettings.OutputJson)
                        {
                            Output<OpenApiJsonWriter>(outputPath, configurationSettings.SerializeAsV2, swagger, swaggerdoc, "json");
                        }
                    }

                    return 0;
                });
            });

            return runner.Run(args);
        }

        private static string GenerateFileName(string path, string version, string suffix) => Path.Combine(path, $"{version}.{suffix}");

        private static void Output<TOpenApiWriter>(string outputPath, bool serializeAsV2, OpenApiDocument openApiDocument, string swaggerdoc, string suffix) where TOpenApiWriter : IOpenApiWriter
        {
            using (var streamWriter = outputPath != null ? File.CreateText(GenerateFileName(outputPath, swaggerdoc, suffix)) : Console.Out)
            {
                var writer = (TOpenApiWriter)Activator.CreateInstance(typeof(TOpenApiWriter), streamWriter);

                if (serializeAsV2)
                {
                    openApiDocument.SerializeAsV2(writer);
                }
                else
                {
                    openApiDocument.SerializeAsV3(writer);
                }

                if (outputPath != null)
                {
                    Console.WriteLine($"Swagger JSON/YAML succesfully written to {GenerateFileName(outputPath, swaggerdoc, suffix)}");
                }
            }
        }

        private static string EscapePath(string path)
        {
            return path.Contains(" ")
                ? "\"" + path + "\""
                : path;
        }

        private static IServiceProvider GetServiceProvider(Assembly startupAssembly)
        {
            if (startupAssembly == null)
            {
                throw new ArgumentNullException(nameof(startupAssembly));
            }

            if (TryGetCustomHost(startupAssembly, "SwaggerHostFactory", "CreateHost", out IHost host))
            {
                return host.Services;
            }

            if (TryGetCustomHost(startupAssembly, "SwaggerWebHostFactory", "CreateWebHost", out IWebHost webHost))
            {
                return webHost.Services;
            }

            var assemblyFolder = Path.GetDirectoryName(startupAssembly.Location);

            return WebHost.CreateDefaultBuilder()
               .UseStartup(startupAssembly.GetName().Name)
               .UseContentRoot(assemblyFolder)
               .Build()
               .Services;
        }

        private static bool TryGetCustomHost<THost>(
            Assembly startupAssembly,
            string factoryClassName,
            string factoryMethodName,
            out THost host)
        {
            // Scan the assembly for any types that match the provided naming convention
            var factoryTypes = startupAssembly.DefinedTypes
                .Where(t => t.Name == factoryClassName)
                .ToList();

            if (!factoryTypes.Any())
            {
                host = default;
                return false;
            }

            if (factoryTypes.Count() > 1)
                throw new InvalidOperationException($"Multiple {factoryClassName} classes detected");

            var factoryMethod = factoryTypes
                .Single()
                .GetMethod(factoryMethodName, BindingFlags.Public | BindingFlags.Static);

            if (factoryMethod == null || factoryMethod.ReturnType != typeof(THost))
                throw new InvalidOperationException(
                    $"{factoryClassName} class detected but does not contain a public static method " +
                    $"called {factoryMethodName} with return type {typeof(THost).Name}");

            host = (THost)factoryMethod.Invoke(null, null);
            return true;
        }
    }
}
