using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Compatibility;

public static class ModConfigXmlCanonicalizer
{
    public static string Canonicalize(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return string.Empty;
        }

        try
        {
            var document = XDocument.Parse(
                xml,
                LoadOptions.None);
            NormalizeElement(document.Root);

            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = false,
                NewLineHandling = NewLineHandling.None
            };

            var builder = new StringBuilder();
            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                document.WriteTo(writer);
            }

            return builder.ToString();
        }
        catch (XmlException)
        {
            return NormalizeText(xml);
        }
    }

    public static string Sha256(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        using SHA256 sha = SHA256.Create();
        return ToHex(sha.ComputeHash(bytes));
    }

    private static void NormalizeElement(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        foreach (XElement child in element.Elements())
        {
            NormalizeElement(child);
        }

        if (!element.HasElements && element.Value is not null)
        {
            element.Value = NormalizeText(element.Value);
        }

        XAttribute[] attributes = element.Attributes()
            .OrderBy(attribute => attribute.Name.NamespaceName, StringComparer.Ordinal)
            .ThenBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal)
            .ToArray();

        if (attributes.Length > 1)
        {
            element.ReplaceAttributes(attributes);
        }
    }

    private static string NormalizeText(string value)
    {
        return (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    private static string ToHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }
}
