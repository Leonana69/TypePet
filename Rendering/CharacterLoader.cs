using System.Collections.Generic;
using MaplePet.Engine;

namespace MaplePet.Rendering;

/// <summary>
/// Bridges the file-only <see cref="CharacterStore"/> to the renderer: turns a character id into
/// decoded <see cref="CharacterSprites"/>. A disk character is loaded from its folder; the built-in
/// default — and any character whose footage fails to decode — falls back to the bundled
/// head+body <c>Assets/DefaultCharacter</c>, so the pet always has something valid to show.
///
/// <paramref name="poses"/> is the set of poses the caller will show: it both bounds the drag
/// hit-test and limits decoding to just those poses (the live pet plays
/// <see cref="CharacterAnimator.ActivePoses"/>; a card thumbnail needs only <c>stand1</c>), so we
/// never decode a character's full footage when a fraction is used. <paramref name="loadExpressions"/>
/// pulls in the Face item's swappable expressions (the live pet wants them for drag reactions; a
/// thumbnail doesn't). The returned sprites own native bitmaps and must be
/// <see cref="CharacterSprites.Dispose">disposed</see> when swapped out.
/// </summary>
public static class CharacterLoader
{
    public static CharacterSprites? Load(CharacterStore store, string? id,
        IReadOnlyCollection<string>? poses, bool loadExpressions = false)
    {
        var entry = store.Get(id);
        if (entry is { IsBuiltIn: false, Directory: { } dir })
        {
            var sprites = CharacterSprites.LoadFromDirectory(dir, hitTestPoses: poses, posesToLoad: poses,
                loadExpressions: loadExpressions);
            if (sprites is not null) return sprites;
            // Footage went missing/corrupt — fall through to the bundled default rather than blanking.
        }

        return CharacterSprites.Load(footageDir: "Assets/DefaultCharacter", hitTestPoses: poses, posesToLoad: poses,
            loadExpressions: loadExpressions);
    }
}
