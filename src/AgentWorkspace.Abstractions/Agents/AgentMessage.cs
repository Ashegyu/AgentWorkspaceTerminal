namespace AgentWorkspace.Abstractions.Agents;

/// <summary>A message sent from the host to the running agent session.</summary>
public sealed record AgentMessage(string Text);
