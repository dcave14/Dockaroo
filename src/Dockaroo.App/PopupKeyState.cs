using System.Windows.Forms;

namespace Dockaroo.App;

/// <summary>
/// Remembers which keys went down while the popup had focus. A digit launches on KeyUp only
/// when its KeyDown arrived here first: a group opened with Win+N receives the KeyUp of N
/// without the KeyDown, and that stray KeyUp must not launch entry N.
/// </summary>
internal sealed class PopupKeyState
{
    private readonly HashSet<Keys> _down = new();

    /// <summary>
    /// Records a KeyDown; true for the first press, false for auto-repeat.
    /// <paramref name="wasAlreadyDown"/> is the WM_KEYDOWN previous-key-state bit: a key that was
    /// already held when the popup got focus (Win+N held past the repeat delay) arrives as
    /// repeats only and is never recorded, so its KeyUp stays ignored.
    /// </summary>
    public bool Press(Keys key, bool wasAlreadyDown = false)
    {
        if (wasAlreadyDown && !_down.Contains(key))
        {
            return false;
        }

        return _down.Add(key);
    }

    /// <summary>True when this key's KeyDown was seen by the popup; the key is then forgotten.</summary>
    public bool Release(Keys key) => _down.Remove(key);
}
