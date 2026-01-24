namespace codex_bridge_server.Bridge;

public interface ITextTranslator
{
    Task<string?> TranslateToZhCnAsync(string input, CancellationToken cancellationToken);
}

