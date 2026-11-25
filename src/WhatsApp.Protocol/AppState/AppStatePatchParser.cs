using Google.Protobuf;
using Whatsapp;
using WhatsApp.Protocol.Binary;

namespace WhatsApp.Protocol.AppState;

public enum CollectionSyncErrorKind
{
    Conflict = 0,
    Fatal = 1,
    Retry = 2
}

public sealed record CollectionSyncError(
    CollectionSyncErrorKind Kind,
    int Code,
    string Text,
    bool HasMorePatches = false);

public sealed record AppStateCollectionPatchList(
    string CollectionName,
    bool HasMorePatches,
    IReadOnlyList<SyncdPatch> Patches,
    ExternalBlobReference? SnapshotReference,
    CollectionSyncError? Error);

/// <summary>
/// Parses <c>&lt;iq type="result"&gt;&lt;sync&gt;&lt;collection ...&gt;</c> responses into typed
/// snapshots, inline patches, and per-collection server errors (<c>409</c>, <c>400/404</c>, retryable).
/// Mirrors <c>wacore/appstate/src/patch_decode.rs</c>.
/// </summary>
public static class AppStatePatchParser
{
    public static IReadOnlyList<AppStateCollectionPatchList> ParseCollections(Node responseNode)
    {
        ArgumentNullException.ThrowIfNull(responseNode);

        Node syncNode = responseNode.Tag == "sync"
            ? responseNode
            : responseNode.GetChild("sync")
                ?? throw new InvalidDataException("Missing <sync> child in AppState response.");

        var lists = new List<AppStateCollectionPatchList>();
        foreach (Node col in syncNode.GetChildren("collection"))
        {
            lists.Add(ParseSingleCollection(col));
        }
        return lists;
    }

    public static AppStateCollectionPatchList ParseSingleCollection(Node collectionNode)
    {
        ArgumentNullException.ThrowIfNull(collectionNode);

        string name = collectionNode.GetAttr("name")
            ?? throw new InvalidDataException("<collection> missing 'name' attribute.");
        bool hasMore = string.Equals(collectionNode.GetAttr("has_more_patches"), "true", StringComparison.OrdinalIgnoreCase);

        CollectionSyncError? error = null;
        if (string.Equals(collectionNode.GetAttr("type"), "error", StringComparison.Ordinal))
        {
            error = ParseCollectionError(collectionNode, hasMore);
        }

        ExternalBlobReference? snapshotRef = null;
        bool unreadableSnapshot = false;
        if (collectionNode.GetChild("snapshot") is { } snapNode)
        {
            if (snapNode.Content is NodeContent.BytesContent snapBytes && snapBytes.Bytes.Length > 0)
            {
                try
                {
                    snapshotRef = ExternalBlobReference.Parser.ParseFrom(snapBytes.Bytes);
                }
                catch (InvalidProtocolBufferException)
                {
                    unreadableSnapshot = true;
                }
            }
            else
            {
                unreadableSnapshot = true;
            }
        }

        var patches = new List<SyncdPatch>();
        if (collectionNode.GetChild("patches") is { } patchesNode)
        {
            foreach (Node patchNode in patchesNode.GetChildren("patch"))
            {
                if (patchNode.Content is NodeContent.BytesContent patchBytes)
                {
                    try
                    {
                        patches.Add(SyncdPatch.Parser.ParseFrom(patchBytes.Bytes));
                    }
                    catch (InvalidProtocolBufferException ex)
                    {
                        throw new InvalidDataException($"Failed to unmarshal SyncdPatch for '{name}'.", ex);
                    }
                }
            }
        }

        error ??= unreadableSnapshot
            ? new CollectionSyncError(CollectionSyncErrorKind.Retry, 0, "unreadable snapshot reference", hasMore)
            : null;

        return new AppStateCollectionPatchList(name, hasMore, patches, snapshotRef, error);
    }

    private static CollectionSyncError ParseCollectionError(Node collectionNode, bool hasMore)
    {
        int code = 0;
        string text = string.Empty;

        if (collectionNode.GetChild("error") is { } errNode)
        {
            _ = int.TryParse(errNode.GetAttr("code"), out code);
            text = errNode.GetAttr("text") ?? string.Empty;
        }
        else
        {
            text = "missing <error> child";
        }

        CollectionSyncErrorKind kind = code switch
        {
            409 => CollectionSyncErrorKind.Conflict,
            400 or 404 => CollectionSyncErrorKind.Fatal,
            _ => CollectionSyncErrorKind.Retry
        };

        return new CollectionSyncError(kind, code, text, hasMore);
    }
}
