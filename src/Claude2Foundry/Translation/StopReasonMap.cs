namespace Claude2Foundry.Translation;

public static class StopReasonMap
{
    public static string Map(string? finishReason) => finishReason switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_calls" => "tool_use",
        "content_filter" => "end_turn",
        _ => "end_turn",
    };
}
