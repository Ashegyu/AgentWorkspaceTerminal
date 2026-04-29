namespace AgentWorkspace.Client.Wire;

/// <summary>
/// Method names carried in the JSON envelope for <see cref="RpcProtocol.OpRequest"/> frames.
/// Centralised so the client and the daemon dispatch table reference the same identifiers.
/// </summary>
public static class RpcMethods
{
    // -- Pane control surface (mirrors IControlChannel + IDataChannel) ----------------------
    public const string StartPane = "pane.start";
    public const string WriteInput = "pane.write";
    public const string ResizePane = "pane.resize";
    public const string SignalPane = "pane.signal";
    public const string ClosePane = "pane.close";
    public const string SubscribeFrames = "pane.subscribe";
    public const string UnsubscribeFrames = "pane.unsubscribe";

    // -- Session-store surface (mirrors ISessionStore) --------------------------------------
    public const string StoreInitialize = "store.initialize";
    public const string StoreCreateSession = "store.create";
    public const string StoreListSessions = "store.list";
    public const string StoreAttachSession = "store.attach";
    public const string StoreUpsertPane = "store.upsert_pane";
    public const string StoreDeletePane = "store.delete_pane";
    public const string StoreSaveLayout = "store.save_layout";
    public const string StoreDeleteSession = "store.delete";

    // -- Agent surface -------------------------------------------------------------------------
    public const string StartAgentSession = "agent.start";

    // -- Diagnostics --------------------------------------------------------------------------
    public const string Ping = "diag.ping";
}
