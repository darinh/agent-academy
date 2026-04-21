using System.Text.Json;
using AgentAcademy.Forge.Artifacts;

namespace AgentAcademy.Forge.Tests;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Serialize_SortsObjectKeysAlphabetically()
    {
        var json = """{"z":1,"a":2,"m":3}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"a":2,"m":3,"z":1}""", result);
    }

    [Fact]
    public void Serialize_SortsNestedObjectKeys()
    {
        var json = """{"b":{"z":1,"a":2},"a":1}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"a":1,"b":{"a":2,"z":1}}""", result);
    }

    [Fact]
    public void Serialize_PreservesArrayOrder()
    {
        var json = """{"items":[3,1,2]}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"items":[3,1,2]}""", result);
    }

    [Fact]
    public void Serialize_RemovesWhitespace()
    {
        var json = """
        {
            "key" : "value",
            "nested" : {
                "inner" : true
            }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"key":"value","nested":{"inner":true}}""", result);
    }

    [Fact]
    public void Serialize_HandlesAllJsonTypes()
    {
        var json = """{"array":[1,2],"bool":true,"null":null,"number":42.5,"string":"hello"}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"array":[1,2],"bool":true,"null":null,"number":42.5,"string":"hello"}""", result);
    }

    [Fact]
    public void Serialize_HandlesFalseBool()
    {
        var json = """{"flag":false}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"flag":false}""", result);
    }

    [Fact]
    public void Serialize_HandlesEmptyObject()
    {
        var json = "{}";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("{}", result);
    }

    [Fact]
    public void Serialize_HandlesEmptyArray()
    {
        var json = """{"items":[]}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"items":[]}""", result);
    }

    [Fact]
    public void Serialize_TypedObject_ProducesCanonicalForm()
    {
        var obj = new { Zebra = 1, Apple = 2 };
        var result = CanonicalJson.Serialize(obj);
        // System.Text.Json camelCase naming + sorted
        Assert.Equal("""{"apple":2,"zebra":1}""", result);
    }

    [Fact]
    public void Hash_ProducesDeterministicSha256()
    {
        var json = """{"key":"value"}""";
        using var doc = JsonDocument.Parse(json);

        var hash1 = CanonicalJson.Hash(doc.RootElement);
        var hash2 = CanonicalJson.Hash(doc.RootElement);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 = 64 hex chars
        Assert.Matches("^[0-9a-f]{64}$", hash1);
    }

    [Fact]
    public void Hash_DifferentKeyOrder_SameHash()
    {
        var json1 = """{"a":1,"b":2}""";
        var json2 = """{"b":2,"a":1}""";

        using var doc1 = JsonDocument.Parse(json1);
        using var doc2 = JsonDocument.Parse(json2);

        Assert.Equal(CanonicalJson.Hash(doc1.RootElement), CanonicalJson.Hash(doc2.RootElement));
    }

    [Fact]
    public void Hash_DifferentValues_DifferentHash()
    {
        var json1 = """{"key":"value1"}""";
        var json2 = """{"key":"value2"}""";

        using var doc1 = JsonDocument.Parse(json1);
        using var doc2 = JsonDocument.Parse(json2);

        Assert.NotEqual(CanonicalJson.Hash(doc1.RootElement), CanonicalJson.Hash(doc2.RootElement));
    }

    [Fact]
    public void PrefixedHash_IncludesSha256Prefix()
    {
        var json = """{"test":true}""";
        using var doc = JsonDocument.Parse(json);

        var prefixed = CanonicalJson.PrefixedHash(doc.RootElement);

        Assert.StartsWith("sha256:", prefixed);
        Assert.Equal(7 + 64, prefixed.Length); // "sha256:" + 64 hex
    }

    [Fact]
    public void StripPrefix_RemovesSha256Prefix()
    {
        var hash = "abc123def456";
        Assert.Equal(hash, CanonicalJson.StripPrefix($"sha256:{hash}"));
    }

    [Fact]
    public void StripPrefix_NoopIfNoPrefix()
    {
        var hash = "abc123def456";
        Assert.Equal(hash, CanonicalJson.StripPrefix(hash));
    }

    [Fact]
    public void Hash_TypedObject_Deterministic()
    {
        var obj = new { Name = "test", Value = 42 };
        var hash1 = CanonicalJson.Hash(obj);
        var hash2 = CanonicalJson.Hash(obj);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Serialize_HandlesUnicodeStrings()
    {
        var json = """{"emoji":"🔥","chinese":"你好"}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        // BMP chars preserved, supplementary plane may be escaped — both are valid
        Assert.Contains("你好", result);
        // Result should be deterministic regardless of encoding
        var result2 = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal(result, result2);
    }

    [Fact]
    public void Serialize_PreservesNumberPrecision()
    {
        var json = """{"pi":3.141592653589793,"big":9007199254740993}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Contains("3.141592653589793", result);
        Assert.Contains("9007199254740993", result);
    }

    [Fact]
    public void Serialize_DeeplyNested()
    {
        var json = """{"a":{"b":{"c":{"d":"leaf"}}}}""";
        using var doc = JsonDocument.Parse(json);
        var result = CanonicalJson.Serialize(doc.RootElement);
        Assert.Equal("""{"a":{"b":{"c":{"d":"leaf"}}}}""", result);
    }
}
