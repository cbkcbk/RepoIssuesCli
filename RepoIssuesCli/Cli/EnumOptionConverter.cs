using System.ComponentModel;
using System.Globalization;

namespace RepoIssuesCli.Cli;

public sealed class EnumOptionConverter<T> : TypeConverter where T : struct, Enum
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        var text = ((string)value).Replace("-", "").Replace("_", "");
        return Enum.TryParse<T>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new UsageException($"'{value}' is not a valid {typeof(T).Name}. Use one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }
}
