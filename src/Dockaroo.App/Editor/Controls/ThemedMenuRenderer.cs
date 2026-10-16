using System.Drawing;
using System.Windows.Forms;
using Dockaroo.Core.Appearance;

namespace Dockaroo.App.Editor.Controls;

/// <summary>Context-menu colours from the theme tokens (row overflow menu).</summary>
internal sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemeTokens _tokens;

    public ThemedMenuRenderer(ThemeTokens tokens)
        : base(new TokenColorTable(tokens))
    {
        _tokens = tokens;
        RoundedEdges = true;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _tokens.Text : _tokens.Faint;
        base.OnRenderItemText(e);
    }

    private sealed class TokenColorTable : ProfessionalColorTable
    {
        private readonly ThemeTokens _t;

        public TokenColorTable(ThemeTokens tokens)
        {
            _t = tokens;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => _t.Surface;

        public override Color ImageMarginGradientBegin => _t.Surface;

        public override Color ImageMarginGradientMiddle => _t.Surface;

        public override Color ImageMarginGradientEnd => _t.Surface;

        public override Color MenuBorder => _t.Border;

        public override Color MenuItemBorder => _t.SelectedBorder;

        public override Color MenuItemSelected => _t.Selected;

        public override Color MenuItemSelectedGradientBegin => _t.Selected;

        public override Color MenuItemSelectedGradientEnd => _t.Selected;

        public override Color SeparatorDark => _t.Border;

        public override Color SeparatorLight => _t.Surface;
    }
}
