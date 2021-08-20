using System.IO;

namespace Swashbuckle.AspNetCore.Cli.Settings
{
    public class ConfigurationSettings
    {
        public bool OutputJson { get; set; }

        public bool OutputYaml { get; set; }

        public bool SerializeAsV2 { get; set; }

        public string Assembly { get; set; }

        public string BasePath { get; set; }

        public string Host { get; set; }

        public string Output { get; set; }

        public string SwaggerDoc { get; set; }

        public bool HasHost => !string.IsNullOrWhiteSpace(Host);

        public bool HasBasePath => !string.IsNullOrWhiteSpace(BasePath);

        public bool LoopThroughVersions => string.IsNullOrWhiteSpace(SwaggerDoc);

        public bool HasOutput => !string.IsNullOrWhiteSpace(Output);

        public string DepsFile => Assembly.Replace(".dll", ".deps.json");

        public string RuntimeConfig => Assembly.Replace(".dll", ".runtimeconfig.json");
    }
}