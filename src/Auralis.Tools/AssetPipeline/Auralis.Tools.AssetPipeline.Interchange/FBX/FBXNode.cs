using System.Globalization;

namespace Auralis.Tools.AssetPipeline.Interchange.FBX;

/// <summary>A node in the generic FBX document tree.</summary>
public sealed class FBXNode
{
    private readonly List<object?> _properties = [];
    private readonly List<FBXNode> _children = [];

    public string Name { get; }
    public IReadOnlyList<object?> Properties => _properties;
    public IReadOnlyList<FBXNode> Children => _children;

    internal FBXNode(string name) => Name = name;

    internal void AddProperty(object? value) => _properties.Add(value);
    internal void AddChild(FBXNode child) => _children.Add(child);

    public FBXNode? GetChild(string name)
    {
        foreach (FBXNode child in _children)
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
                return child;
        return null;
    }

    public IEnumerable<FBXNode> GetChildren(string name)
    {
        foreach (FBXNode child in _children)
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
                yield return child;
    }

    public string? GetString(int index)
    {
        if ((uint)index >= (uint)_properties.Count || _properties[index] is null)
            return null;
        return Convert.ToString(_properties[index], CultureInfo.InvariantCulture);
    }

    public long GetInt64(int index, long fallback = 0)
    {
        if ((uint)index >= (uint)_properties.Count || _properties[index] is null)
            return fallback;
        try { return Convert.ToInt64(_properties[index], CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return fallback; }
    }

    public double GetDouble(int index, double fallback = 0)
    {
        if ((uint)index >= (uint)_properties.Count || _properties[index] is null)
            return fallback;
        try { return Convert.ToDouble(_properties[index], CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return fallback; }
    }

    /// <summary>Gets an FBX array property, including ASCII arrays stored below an <c>a</c> child node.</summary>
    public Array? GetArray(string propertyName)
    {
        FBXNode? property = GetChild(propertyName);
        if (property is null)
            return null;

        foreach (object? value in property._properties)
            if (value is Array array)
                return array;

        FBXNode? asciiArray = property.GetChild("a");
        if (asciiArray is not null)
        {
            foreach (object? value in asciiArray._properties)
                if (value is Array array)
                    return array;

            if (asciiArray._properties.Count > 0)
                return asciiArray._properties.ToArray();
        }

        if (property._properties.Count == 1 && property._properties[0] is Array direct)
            return direct;
        return null;
    }

    /// <summary>Returns a numeric array without converting it when it is already a double array.</summary>
    public double[]? GetDoubleArray(string propertyName)
    {
        Array? value = GetArray(propertyName);
        if (value is null) return null;
        if (value is double[] doubles) return doubles;

        var result = new double[value.Length];
        for (int i = 0; i < result.Length; i++)
        {
            try { result[i] = Convert.ToDouble(value.GetValue(i), CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            { throw new FBXFormatException($"Property '{propertyName}' contains a non-numeric array value."); }
        }
        return result;
    }

    /// <summary>Returns an integer array, converting the source FBX numeric representation if necessary.</summary>
    public int[]? GetInt32Array(string propertyName)
    {
        Array? value = GetArray(propertyName);
        if (value is null) return null;
        if (value is int[] integers) return integers;

        var result = new int[value.Length];
        for (int i = 0; i < result.Length; i++)
        {
            try { result[i] = Convert.ToInt32(value.GetValue(i), CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            { throw new FBXFormatException($"Property '{propertyName}' contains a non-integer array value."); }
        }
        return result;
    }
}
