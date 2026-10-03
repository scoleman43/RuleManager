namespace RuleManager.Core.Domain;

public static class CategoryName
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Category name is required.", nameof(value));

        var parts = value
            .Split(':', 2, StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length == 0)
            throw new ArgumentException("Category name is required.", nameof(value));

        return string.Join(':', parts);
    }

    public static bool HasSubCategory(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(':');
}
