using WzSync.Release;

if (args is not [var directory, var tag])
{
    Console.Error.WriteLine("Usage: WzSync.Release <bundle-directory> <release-tag>");
    return 2;
}

try
{
    var count = ReleaseBundle.Validate(directory, tag);
    Console.WriteLine($"Validated both manifest/checksum pairs and {count} owned assets.");
    return 0;
}
catch (InvalidDataException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine("Could not read the release input.");
    return 1;
}
