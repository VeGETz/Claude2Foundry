using System.Collections.Concurrent;
using Claude2Foundry.Config;
using Microsoft.ML.Tokenizers;

namespace Claude2Foundry.Tokens;

public static class TokenizerRegistry
{
    private static readonly ConcurrentDictionary<string, Tokenizer> _cache = new();

    public static Tokenizer Get(TokenizerConfig cfg)
    {
        var key = $"{cfg.Source}:{cfg.Path ?? ""}";
        return _cache.GetOrAdd(key, _ => Load(cfg));
    }

    private static Tokenizer Load(TokenizerConfig cfg) => cfg.Source switch
    {
        "TiktokenCl100k" => TiktokenTokenizer.CreateForModel("gpt-4"),
        "TiktokenO200k" => TiktokenTokenizer.CreateForModel("gpt-4o"),
        "HuggingFace" => LoadHuggingFace(cfg.Path!),
        _ => throw new InvalidOperationException($"Unknown tokenizer source: {cfg.Source}")
    };

    private static Tokenizer LoadHuggingFace(string path)
    {
        using var stream = File.OpenRead(path);
        // SentencePiece / Llama format (.model file)
        return LlamaTokenizer.Create(stream);
    }
}
