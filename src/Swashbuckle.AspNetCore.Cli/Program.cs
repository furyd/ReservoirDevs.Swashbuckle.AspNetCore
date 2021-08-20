﻿using System;
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
                c.Argument("startupassembly", "relative path to the application's startup assembly");
                c.Option("--swaggerdoc", "name of the swagger doc you want to retrieve, as configured in your startup class");
                c.Option("--output", "relative path where the Swagger will be output, defaults to stdout");
                c.Option("--host", "a specific host to include in the Swagger output");
                c.Option("--basepath", "a specific basePath to include in the Swagger output");
                c.Option("--serializeasv2", "output Swagger in the V2 format rather than V3", true);
                c.Option("--json", "exports swagger in a json format", true);
                c.Option("--yaml", "exports swagger in a yaml format", true);
                c.OnRun((namedArgs) =>
                {
                    if (!File.Exists(namedArgs["startupassembly"]))
                    {
                        throw new FileNotFoundException(namedArgs["startupassembly"]);
                    }


                    var depsFile = namedArgs["startupassembly"].Replace(".dll", ".deps.json");
                    var runtimeConfig = namedArgs["startupassembly"].Replace(".dll", ".runtimeconfig.json");
                    var commandName = args[0];

                    var subProcessArguments = new string[args.Length - 1];
                    if (subProcessArguments.Length > 0)
                    {
                        Array.Copy(args, 1, subProcessArguments, 0, subProcessArguments.Length);
                    }

                    var subProcessCommandLine = string.Format(
                        "exec --depsfile {0} --runtimeconfig {1} {2} _{3} {4}", // note the underscore prepended to the command name
                        EscapePath(depsFile),
                        EscapePath(runtimeConfig),
                        EscapePath(typeof(Program).GetTypeInfo().Assembly.Location),
                        commandName,
                        string.Join(" ", subProcessArguments.Select(EscapePath))
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
                c.Argument("startupassembly", "");
                c.Option("--swaggerdoc", "");
                c.Option("--output", "");
                c.Option("--host", "");
                c.Option("--basepath", "");
                c.Option("--serializeasv2", "", true);
                c.Option("--yaml", "", true);
                c.Option("--json", "", true);
                c.OnRun((namedArgs) =>
                {
                    // 1) Configure host with provided startupassembly
                    var startupAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Directory.GetCurrentDirectory(), namedArgs["startupassembly"]));

                    // 2) Build a service container that's based on the startup assembly
                    var serviceProvider = GetServiceProvider(startupAssembly);

                    var swaggerdocs = new List<string>();

                    if (namedArgs.ContainsKey("--swaggerdoc") && !string.IsNullOrWhiteSpace(namedArgs["--swaggerdoc"]))
                    {
                        Console.WriteLine($"Version defined: {namedArgs["--swaggerdoc"]}");
                        swaggerdocs.Add(namedArgs["--swaggerdoc"]);
                    }
                    else
                    {
                        Console.WriteLine("Version not defined, extract from IApiVersionDescriptionProvider");
                        var provider = serviceProvider.GetRequiredService<IApiVersionDescriptionProvider>();
                        swaggerdocs.AddRange(provider.ApiVersionDescriptions.Select(item => item.GroupName));
                    }

                    // 3) Retrieve Swagger via configured provider

                    foreach (var swaggerdoc in swaggerdocs)
                    {
                        var swaggerProvider = serviceProvider.GetRequiredService<ISwaggerProvider>();
                        var swagger = swaggerProvider.GetSwagger(
                            swaggerdoc,
                            namedArgs.ContainsKey("--host") ? namedArgs["--host"] : null,
                            namedArgs.ContainsKey("--basepath") ? namedArgs["--basepath"] : null);

                        // 4) Serialize to specified output location or stdout
                        string outputPath = null;

                        if (namedArgs.ContainsKey("--output") && !Directory.Exists(namedArgs["--output"]))
                        {
                            throw new DirectoryNotFoundException($"{namedArgs["--output"]} does not exist");
                        }

                        if (namedArgs.ContainsKey("--output"))
                        {
                            outputPath = Path.Join(namedArgs["--output"]);
                            Console.WriteLine($"Path: {outputPath}");
                        }

                        if (namedArgs.ContainsKey("--yaml"))
                        {
                            Output<OpenApiYamlWriter>(outputPath, namedArgs.ContainsKey("--serializeasv2"), swagger, swaggerdoc, "yaml");
                        }

                        if (namedArgs.ContainsKey("--json"))
                        {
                            Output<OpenApiJsonWriter>(outputPath, namedArgs.ContainsKey("--serializeasv2"), swagger, swaggerdoc, "json");
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
