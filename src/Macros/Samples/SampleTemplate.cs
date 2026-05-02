using System;

namespace Macros.Samples;

public sealed class SampleTemplate
{
    public SampleTemplate(string name, string description, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Template name must be non-empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Template description must be non-empty.", nameof(description));
        }

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new ArgumentException("Template resource name must be non-empty.", nameof(resourceName));
        }

        Name = name;
        Description = description;
        ResourceName = resourceName;
    }

    public string Name { get; }

    public string Description { get; }

    public string ResourceName { get; }
}
