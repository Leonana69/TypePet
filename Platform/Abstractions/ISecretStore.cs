namespace TypePet.Platform.Abstractions;

/// <summary>
/// Per-user, encrypted-at-rest storage for secrets the chatbot needs — provider API keys and the
/// web-search key. Kept OUT of settings.json (which is plaintext JSON the user can open): Windows
/// encrypts with DPAPI, macOS uses the login Keychain. Identified by a stable string id (e.g. a
/// provider profile id, or the reserved <c>"search"</c>). All methods are best-effort and never throw —
/// a missing/locked store returns null so the chatbot can prompt for the key again.
/// </summary>
public interface ISecretStore
{
    /// <summary>Return the secret for <paramref name="id"/>, or null if none is stored / it can't be read.</summary>
    string? Get(string id);

    /// <summary>Store (or replace) the secret for <paramref name="id"/>. A null/empty value deletes it.</summary>
    void Set(string id, string? secret);

    /// <summary>Remove the secret for <paramref name="id"/> if present.</summary>
    void Delete(string id);
}
