# GUI Visual Regression Testing

A successful WPF compilation or property assertion is not sufficient validation. Run the following command to render .NET 8/10, light/dark themes, normal/minimum widths, every main tab, and analyzer/source-generator table, tree, and empty states in real windows:

```powershell
./eng/build.ps1 visual --output artifacts/gui-visuals
```

`artifacts/gui-visuals/index.html` links both TFMs. Each TFM index displays every image at the same dimensions in a grid. Missing images fail the command. Outputs are ignored by Git.

## Required comparison order

1. Compare light and dark side by side. Check selected, unselected, hover/focus, and disabled text and boundaries. After switching between table and tree selection, inspect all four sides of the focus rectangle.
2. Compare normal and minimum widths. Check right/bottom edges, corners, scrollbars, and the final row for clipping.
3. Compare analyzer and source-generator tables, then their trees. Check headers, separators, spacing, row height, and empty states.
4. Inspect all seven main tabs in order for consistent tab corners, selection underline, content boundary, and search/action alignment.
5. Compare enabled and disabled target toolbars. Check the target input, Browse, Recent, Advanced, configuration, and Start button heights, borders, and spacing.
6. Inspect the entire window, not only the changed control, for new clipping or isolated color, corner, emphasis, or spacing.

## Automated guards

`Yaap.Gui.Tests` fails when:

- Table/tree tabs lack the 1-DIP right drawing margin needed to preserve the outside pixel and top-right radius at 100%, 125%, 150%, and 200% scaling.
- Any side of the independent inner focus rectangle lacks accent-colored pixels after table/tree round trips.
- Main tabs have rounded lower corners, no selection underline, or an entirely accent-colored selected surface.
- Browse, Recent, Advanced, and configuration borders differ or cannot be distinguished from the background.
- Analyzer/source-generator result surfaces, headers, or shared table/tree styles diverge.
- Content escapes its parent, an unnecessary horizontal scrollbar appears, or a partial row is displayed at normal/minimum widths.

Screenshots are comparison inputs, not proof by themselves. Automated success does not replace the ordered visual inspection above.
