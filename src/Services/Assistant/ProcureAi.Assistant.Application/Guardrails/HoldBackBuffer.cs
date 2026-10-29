using Microsoft.Extensions.AI;

namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// The hold-back buffer of the streaming output guard: streamed text is passed on only once it is <c>holdBack</c> characters behind the
/// newest text, so a secret the guard finds later has not left yet. Updates are split into runs of text and runs of other contents (tool
/// calls, usage), which keep their order: a tool call that came after held text waits behind it. One instance per answer; not thread-safe.
/// </summary>
public sealed class HoldBackBuffer(int holdBack)
{
    private readonly LinkedList<Piece> _pieces = new();
    private int _heldText;

    /// <summary>Queues an update's contents, in order.</summary>
    public void Add(ChatResponseUpdate update)
    {
        var run = new List<AIContent>();
        var last = update.Contents.Count - 1;
        for (var i = 0; i <= last; i++)
        {
            var content = update.Contents[i];
            if (content is TextContent text)
            {
                if (run.Count > 0)
                {
                    _pieces.AddLast(new Piece(update, null, run, false));
                    run = [];
                }

                if (!string.IsNullOrEmpty(text.Text))
                {
                    _pieces.AddLast(new Piece(update, text.Text, null, false));
                    _heldText += text.Text.Length;
                }
            }
            else
            {
                run.Add(content);
            }
        }

        if (run.Count > 0)
        {
            _pieces.AddLast(new Piece(update, null, run, false));
        }

        // An update with no contents (or only empty text) still carries metadata such as the finish reason: keep it in order.
        if (_pieces.Count == 0 || _pieces.Last!.Value.Source != update)
        {
            _pieces.AddLast(new Piece(update, null, [], false));
        }

        // The finish reason goes with the last piece of its update only.
        var tail = _pieces.Last!.Value;
        _pieces.Last.Value = tail with { CarriesFinish = true };
    }

    /// <summary>What may be passed on now: other contents at the front, and text more than <c>holdBack</c> characters behind the newest.</summary>
    public IEnumerable<ChatResponseUpdate> Release()
    {
        var releasable = _heldText - holdBack;
        while (_pieces.First is { } node)
        {
            var piece = node.Value;
            if (piece.Text is null)
            {
                _pieces.RemoveFirst();
                yield return piece.ToUpdate();
                continue;
            }

            if (releasable <= 0)
            {
                yield break;
            }

            if (piece.Text.Length <= releasable)
            {
                _pieces.RemoveFirst();
                releasable -= piece.Text.Length;
                _heldText -= piece.Text.Length;
                yield return piece.ToUpdate();
                continue;
            }

            // Part of this text is old enough: pass that part on and keep the rest at the front.
            var part = piece.Text[..releasable];
            node.Value = piece with { Text = piece.Text[releasable..] };
            _heldText -= releasable;
            yield return (piece with { Text = part, CarriesFinish = false }).ToUpdate();
            yield break;
        }
    }

    /// <summary>Everything still held, in order (when the stream ended without a hit).</summary>
    public IEnumerable<ChatResponseUpdate> Flush()
    {
        while (_pieces.First is { } node)
        {
            _pieces.RemoveFirst();
            yield return node.Value.ToUpdate();
        }

        _heldText = 0;
    }

    private sealed record Piece(ChatResponseUpdate Source, string? Text, IList<AIContent>? Others, bool CarriesFinish)
    {
        public ChatResponseUpdate ToUpdate() =>
            new(Source.Role, Text is not null ? [new TextContent(Text)] : [.. Others ?? []])
            {
                AuthorName = Source.AuthorName,
                ResponseId = Source.ResponseId,
                MessageId = Source.MessageId,
                ConversationId = Source.ConversationId,
                ModelId = Source.ModelId,
                CreatedAt = Source.CreatedAt,
                AdditionalProperties = Source.AdditionalProperties,
                FinishReason = CarriesFinish ? Source.FinishReason : null,
            };
    }
}
