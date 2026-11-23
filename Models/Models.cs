using System.Collections.ObjectModel;
using Google.Protobuf.Reflection;

namespace ProtoDeck.Models;

public sealed class SavedRequest
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public bool Insecure { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public string Service { get; set; } = "";
    public string Method { get; set; } = "";
    public string Body { get; set; } = "{}";
    public string Metadata { get; set; } = "";
}

public sealed class RequestCollection
{
    public string Name { get; set; } = "";
    public List<SavedRequest> Requests { get; set; } = [];
}

/// <summary>Node for both the service tree and the collection tree.</summary>
public sealed class TreeNode
{
    public required string Title { get; init; }
    public ObservableCollection<TreeNode> Children { get; } = [];
    public MethodDescriptor? Method { get; init; }
    public SavedRequest? Request { get; init; }
    public RequestCollection? Collection { get; init; }
}
