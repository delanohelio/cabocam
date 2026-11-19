using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Linq;
using System.Xml.Linq;

namespace CaboCam.Usbmux;

/// <summary>
/// Minimal XML property list reader/writer (dict, array, string, integer, real, true/false, data, date).
/// Only what usbmuxd needs.
/// </summary>
public static class Plist
{
    public static byte[] Write(IDictionary<string, object?> root)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = false,
        };
        using (var w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument();
            w.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            w.WriteStartElement("plist");
            w.WriteAttributeString("version", "1.0");
            WriteValue(w, root);
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return ms.ToArray();
    }

    private static void WriteValue(XmlWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.WriteElementString("string", string.Empty);
                break;
            case string s:
                w.WriteElementString("string", s);
                break;
            case bool b:
                w.WriteStartElement(b ? "true" : "false");
                w.WriteEndElement();
                break;
            case int or long or short or byte or uint or ushort or sbyte or ulong:
                w.WriteElementString("integer", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case float or double:
                w.WriteElementString("real", Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                break;
            case byte[] data:
                w.WriteElementString("data", Convert.ToBase64String(data));
                break;
            case IDictionary<string, object?> dict:
                w.WriteStartElement("dict");
                foreach (var kv in dict)
                {
                    w.WriteElementString("key", kv.Key);
                    WriteValue(w, kv.Value);
                }
                w.WriteEndElement();
                break;
            case IEnumerable seq:
                w.WriteStartElement("array");
                foreach (var item in seq) WriteValue(w, item);
                w.WriteEndElement();
                break;
            default:
                throw new NotSupportedException($"Tipo não suportado em plist: {value.GetType()}");
        }
    }

    /// <summary>Parses an XML plist whose root value is a dict.</summary>
    public static Dictionary<string, object?> ParseDict(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var ms = new MemoryStream(data);
        using var reader = XmlReader.Create(ms, settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidDataException("plist vazio");
        var first = root.Elements().FirstOrDefault();
        if (first is null || first.Name.LocalName != "dict")
            throw new InvalidDataException("plist raiz não é um dict");
        return (Dictionary<string, object?>)ReadValue(first)!;
    }

    private static object? ReadValue(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
            {
                var d = new Dictionary<string, object?>();
                string? key = null;
                foreach (var child in e.Elements())
                {
                    if (child.Name.LocalName == "key") key = child.Value;
                    else if (key is not null) { d[key] = ReadValue(child); key = null; }
                }
                return d;
            }
            case "array":
            {
                var list = new List<object?>();
                foreach (var child in e.Elements()) list.Add(ReadValue(child));
                return list;
            }
            case "string": return e.Value;
            case "integer":
                if (long.TryParse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                return double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl) ? dbl : 0L;
            case "real":
                return double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0.0;
            case "true": return true;
            case "false": return false;
            case "data":
                try { return Convert.FromBase64String(e.Value); } catch (FormatException) { return Array.Empty<byte>(); }
            case "date": return e.Value;
            default: return null;
        }
    }

    // Typed accessors -----------------------------------------------------------
    public static string? GetString(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) ? v as string : null;

    public static long GetInt(IReadOnlyDictionary<string, object?> d, string key, long fallback = 0)
        => d.TryGetValue(key, out var v) && v is long l ? l : fallback;

    public static Dictionary<string, object?>? GetDict(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;

    public static List<object?>? GetArray(IReadOnlyDictionary<string, object?> d, string key)
        => d.TryGetValue(key, out var v) ? v as List<object?> : null;
}
