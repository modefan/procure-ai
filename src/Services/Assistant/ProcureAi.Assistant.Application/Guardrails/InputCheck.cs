namespace ProcureAi.Assistant.Application.Guardrails;

/// <summary>
/// Tells the caller of the <c>answer</c> client whether the input guard checked the question and let it through. Pass one in
/// <c>ChatOptions.AdditionalProperties[GuardrailChatClient.InputCheckProperty]</c>; <see cref="GuardrailChatClient"/> sets
/// <see cref="Passed"/> only after the prompt guard allowed the question, and never passes the object on to the model.
/// </summary>
public sealed class InputCheck
{
    private int _passed;

    public bool Passed => Volatile.Read(ref _passed) == 1;

    internal void Pass() => Volatile.Write(ref _passed, 1);
}
