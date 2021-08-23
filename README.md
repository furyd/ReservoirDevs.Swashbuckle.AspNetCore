ReservoirDevs.Swashbuckle.AspNetCore
=========

Based on the excellent work at [the Swashbuckle.AspNetCore](https://github.com/domaindrivendev/Swashbuckle.AspNetCore), this is a modified version of their CLI tool that:

- Loads any appsettings from the same location as the target DLL
- Can generate the JSON and YAML outputs simultaneously
- Can extract _all_ API versions and generate output for each version in a single call

This was forked and updated primarily so that I didn't need to know what versions to use _beforehand_, and just make use of IApiVersionDescriptionProvider instead.

Usage
-----

I've replaced all the optional switches and DLL arguments with a single argument which is the absolute location of a configuration file.

The configuration file takes the following structure:

```json
{
    "Assembly": "<<REQUIRED: absolute location of the DLL>>",
    "OutputJson": true/false,
    "OutputYaml": true/false,
    "SerializeAsV2": true/false,
    "BasePath": "<<OPTIONAL: a specific basePath to include in the Swagger output>>",
    "Host": "<<OPTIONAL: a specific host to include in the Swagger output>>",
    "Output": "<<OPTIONAL: absolute path of the directory to emit the files>>",
    "SwaggerDoc": "<<OPTIONAL: name of the swagger doc you want to retrieve, as configured in your startup class>>"
}
```

The booleans will default to false.

If SwaggerDoc is null, empty or whitespace, then all available versions from IApiVersionDescriptionProvider will be outputted.

Files are outputted as <<version>>.<<format>>, e.g. v1's JSON will be output as v1.json, YAML, v1.yaml in the directory specified in the Output property, or output to stream if the Output property is not set.


TODO
----

- Add a appsettings folder location property
- Validate the configuration file