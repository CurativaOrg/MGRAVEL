using Gremlin.Net.Driver;
using Gremlin.Net.Driver.Messages;
using Gremlin.Net.Process;
using Gremlin.Net.Structure;
using System.Text;
using System.Collections;

namespace DAL.Repositories;

public sealed record GraphVertex(string Id, string Label, IDictionary<string, object> Properties);

public sealed record GraphEdge(string Id, string Label, string OutVertexId, string InVertexId, IDictionary<string, object> Properties);

public interface IGraphRepository
{
    Task<GraphVertex> AddVertexAsync(string label, IDictionary<string, object> properties, CancellationToken ct = default);
    Task<GraphEdge> AddEdgeAsync(string label, string outVertexId, string inVertexId, IDictionary<string, object>? properties = null, CancellationToken ct = default);
    Task<GraphVertex?> GetVertexByIdAsync(string id, CancellationToken ct = default);
    Task<bool> UpdateVertexPropertiesAsync(string id, IDictionary<string, object> properties, CancellationToken ct = default);
    Task<bool> DeleteVertexAsync(string id, CancellationToken ct = default);
    Task<long> CountVerticesAsync(CancellationToken ct = default);
    Task<GraphEdge?> AddEdgeByPropertyAsync(
        string label,
        string outLabel, string outKey, object outValue,
        string inLabel, string inKey, object inValue,
        IDictionary<string, object>? properties = null,
        CancellationToken ct = default);
    Task<GraphVertex> UpsertVertexByPropertyAsync(string label, string key, object value, IDictionary<string, object> properties, CancellationToken ct = default);
    Task<string?> AddVertexAndReturnIdAsync(string label, IDictionary<string, object> properties, CancellationToken ct = default);
    Task<string?> UpsertVertexAndReturnIdAsync(string label, string key, object value, IDictionary<string, object> properties, CancellationToken ct = default);
}

internal sealed class GraphRepository : IGraphRepository
{
    private readonly GremlinClient _client;

    public GraphRepository(GremlinClient client)
    {
        _client = client;
    }

    private static void AppendProperties(StringBuilder script, Dictionary<string, object> bindings, IDictionary<string, object>? properties)
    {
        if (properties is null || properties.Count == 0)
            return;

        foreach (var (key, value) in properties)
        {
            var bindingKey = $"p_{key}";
            script.Append($".property('{key}', {bindingKey})");
            bindings[bindingKey] = value!;
        }
    }

    private async Task<IReadOnlyCollection<T>> SubmitAsync<T>(string script, Dictionary<string, object>? bindings, CancellationToken ct)
    {
        var messageBuilder = RequestMessage.Build(Tokens.OpsEval)
            .AddArgument(Tokens.ArgsGremlin, script);

        if (bindings is not null && bindings.Count > 0)
        {
            messageBuilder = messageBuilder.AddArgument(Tokens.ArgsBindings, bindings);
        }

        var message = messageBuilder.Create();
        return await _client.SubmitAsync<T>(message, ct);
    }

    private static IDictionary<string, object> MaterializeProperties(object? input)
    {
        if (input is null)
            return new Dictionary<string, object>();

        if (input is IDictionary dict)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dict)
            {
                var key = entry.Key?.ToString();
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                result[key] = entry.Value!;
            }
            return result;
        }

        // Some Gremlin.Net shapes can come back as arrays/enumerables of properties.
        // Represent as a single "value" field rather than leaking Gremlin types.
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["value"] = input
        };
    }

    private static GraphVertex ToGraphVertex(Vertex v)
    {
        var id = v.Id?.ToString() ?? string.Empty;
        var props = MaterializeProperties(v.Properties);
        return new GraphVertex(id, v.Label ?? string.Empty, props);
    }

    private static GraphEdge ToGraphEdge(Edge e)
    {
        var id = e.Id?.ToString() ?? string.Empty;
        var outV = e.OutV?.Id?.ToString() ?? string.Empty;
        var inV = e.InV?.Id?.ToString() ?? string.Empty;

        var props = MaterializeProperties(e.Properties);

        return new GraphEdge(id, e.Label ?? string.Empty, outV, inV, props);
    }

    public async Task<GraphVertex> AddVertexAsync(string label, IDictionary<string, object> properties, CancellationToken ct = default)
    {
        var script = new StringBuilder("g.addV(label)");
        var bindings = new Dictionary<string, object> { ["label"] = label };

        AppendProperties(script, bindings, properties);

        var result = await SubmitAsync<Vertex>(script.ToString(), bindings, ct);
        return ToGraphVertex(result.First());
    }

    public async Task<string?> AddVertexAndReturnIdAsync(string label, IDictionary<string, object> properties, CancellationToken ct = default)
    {
        var script = new StringBuilder("g.addV(label)");
        var bindings = new Dictionary<string, object> { ["label"] = label };

        AppendProperties(script, bindings, properties);

        script.Append(".id()");
        var result = await SubmitAsync<object>(script.ToString(), bindings, ct);
        return result.FirstOrDefault()?.ToString();
    }

    public async Task<GraphEdge> AddEdgeAsync(string label, string outVertexId, string inVertexId, IDictionary<string, object>? properties = null, CancellationToken ct = default)
    {
        var script = new StringBuilder("g.V(outId).addE(label).to(g.V(inId))");
        var bindings = new Dictionary<string, object>
        {
            ["label"] = label,
            ["outId"] = outVertexId,
            ["inId"] = inVertexId
        };

        AppendProperties(script, bindings, properties);

        var result = await SubmitAsync<Edge>(script.ToString(), bindings, ct);
        return ToGraphEdge(result.First());
    }

    public async Task<GraphVertex?> GetVertexByIdAsync(string id, CancellationToken ct = default)
    {
        var result = await SubmitAsync<Vertex>("g.V(id)", new Dictionary<string, object> { ["id"] = id }, ct);
        var v = result.FirstOrDefault();
        return v is null ? null : ToGraphVertex(v);
    }

    public async Task<bool> UpdateVertexPropertiesAsync(string id, IDictionary<string, object> properties, CancellationToken ct = default)
    {
        var script = new StringBuilder("g.V(id)");
        var bindings = new Dictionary<string, object> { ["id"] = id };

        AppendProperties(script, bindings, properties);

        var result = await SubmitAsync<Vertex>(script.ToString(), bindings, ct);
        return result.Any();
    }

    public async Task<bool> DeleteVertexAsync(string id, CancellationToken ct = default)
    {
        var result = await SubmitAsync<object>("g.V(id).drop()", new Dictionary<string, object> { ["id"] = id }, ct);
        // JanusGraph returns empty result set for drop; treat as success if no exception
        return true;
    }

    public async Task<long> CountVerticesAsync(CancellationToken ct = default)
    {
        var result = await SubmitAsync<long>("g.V().count()", null, ct);
        return result.FirstOrDefault();
    }

    public async Task<GraphEdge?> AddEdgeByPropertyAsync(
        string label,
        string outLabel, string outKey, object outValue,
        string inLabel, string inKey, object inValue,
        IDictionary<string, object>? properties = null,
        CancellationToken ct = default)
    {
        var script = new StringBuilder("g.V().has(outLabel, outKey, outVal).addE(label).to(g.V().has(inLabel, inKey, inVal))");
        var bindings = new Dictionary<string, object>
        {
            ["label"] = label,
            ["outLabel"] = outLabel,
            ["outKey"] = outKey,
            ["outVal"] = outValue,
            ["inLabel"] = inLabel,
            ["inKey"] = inKey,
            ["inVal"] = inValue
        };

        AppendProperties(script, bindings, properties);

        var result = await SubmitAsync<Edge>(script.ToString(), bindings, ct);
        var e = result.FirstOrDefault();
        return e is null ? null : ToGraphEdge(e);
    }

    public async Task<GraphVertex> UpsertVertexByPropertyAsync(string label, string key, object value, IDictionary<string, object> properties, CancellationToken ct = default)
    {
        var script = new StringBuilder("g.V().has(lbl, propKey, propVal).fold().coalesce(unfold(), addV(lbl).property(propKey, propVal))");
        var bindings = new Dictionary<string, object>
        {
            ["lbl"] = label,
            ["propKey"] = key,
            ["propVal"] = value
        };

        AppendProperties(script, bindings, properties);

        var result = await SubmitAsync<Vertex>(script.ToString(), bindings, ct);
        return ToGraphVertex(result.First());
    }

    public async Task<string?> UpsertVertexAndReturnIdAsync(string label, string key, object value, IDictionary<string, object> properties, CancellationToken ct = default)
    {
        var script = new StringBuilder("g.V().has(lbl, propKey, propVal).fold().coalesce(unfold(), addV(lbl).property(propKey, propVal))");
        var bindings = new Dictionary<string, object>
        {
            ["lbl"] = label,
            ["propKey"] = key,
            ["propVal"] = value
        };

        AppendProperties(script, bindings, properties);

        script.Append(".id()");
        var result = await SubmitAsync<object>(script.ToString(), bindings, ct);
        return result.FirstOrDefault()?.ToString();
    }
}
