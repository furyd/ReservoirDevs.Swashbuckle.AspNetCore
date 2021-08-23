using System;
using System.IO;
using FluentValidation;
using Swashbuckle.AspNetCore.Cli.Settings;

namespace Swashbuckle.AspNetCore.Cli.Validators
{
    public class ConfigurationSettingsValidator : AbstractValidator<ConfigurationSettings>
    {
        public ConfigurationSettingsValidator()
        {
            RuleFor(model => model.Assembly)
                .NotEmpty().WithMessage("Assembly is required")
                .Must(File.Exists).WithMessage("Assembly file must exist");

            RuleFor(model => model.Output)
                .Must(Directory.Exists).WithMessage("Output folder must exist").When(model => !string.IsNullOrWhiteSpace(model.Output));

            RuleFor(model => model.Host)
                .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out _)).WithMessage("Host must be a valid URI").When(model => !string.IsNullOrWhiteSpace(model.Host));
        }
    }
}