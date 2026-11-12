import { RGBA, SyntaxStyle } from "@opentui/core";
import type { ThemeColors } from "../theme";

// How a reply's markdown looks: headings, emphasis, code and links in the
// theme's colours, and the tokens of fenced code. One style object per theme —
// it lives in native memory, so it is made once and kept.

const styles = new Map<string, SyntaxStyle>();

export function markdownStyle(colors: ThemeColors): SyntaxStyle {
  const key = JSON.stringify(colors);
  const known = styles.get(key);
  if (known) return known;

  const c = (hex: string) => RGBA.fromHex(hex);
  const style = SyntaxStyle.fromStyles({
    default: {},
    conceal: { fg: c(colors.dimSeparator) },

    "markup.heading": { fg: c(colors.primary), bold: true },
    "markup.strong": { bold: true },
    "markup.italic": { italic: true },
    "markup.strikethrough": { dim: true },
    "markup.raw": { fg: c(colors.info) },
    "markup.link": { fg: c(colors.selection) },
    "markup.link.label": { fg: c(colors.selection), underline: true },
    // The address is always shown beside the label: where a link goes is not hidden behind its words.
    "markup.link.url": { fg: c(colors.dimSeparator) },
    "markup.list": { fg: c(colors.primary) },
    "markup.quote": { fg: c(colors.thinking), italic: true },

    comment: { fg: c(colors.dimSeparator), italic: true },
    keyword: { fg: c(colors.planMode) },
    string: { fg: c(colors.success) },
    number: { fg: c(colors.primary) },
    boolean: { fg: c(colors.primary) },
    constant: { fg: c(colors.primary) },
    function: { fg: c(colors.selection) },
    type: { fg: c(colors.info) },
    tag: { fg: c(colors.info) },
    attribute: { fg: c(colors.selection) },
    property: { fg: c(colors.selection) },
    operator: { fg: c(colors.dimSeparator) },
    punctuation: { fg: c(colors.dimSeparator) },
  });
  styles.set(key, style);
  return style;
}
