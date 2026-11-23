# Bundled fonts

All three families are under the SIL Open Font License 1.1; each license sits next to its files.

Keep one folder per family. Avalonia looks for a missing glyph in the whole folder before trying the next family in the list, so with a shared folder Cyrillic and Greek names in UI text came out in JetBrains Mono instead of Inter.

| Family | Files | Source |
| --- | --- | --- |
| Outfit | Regular, Medium, SemiBold, Bold, ExtraBold | static instances of `Outfit[wght].ttf` from [google/fonts](https://github.com/google/fonts/tree/main/ofl/outfit) |
| Quicksand | Light, Regular, Medium, Bold | unmodified statics from [QuicksandFamily](https://github.com/andrew-paglinawan/QuicksandFamily/tree/master/fonts/statics) |
| JetBrains Mono | Regular, Bold | static instances of `JetBrainsMono[wght].ttf` from [google/fonts](https://github.com/google/fonts/tree/main/ofl/jetbrainsmono) |

Static files are used because Avalonia doesn't reliably pick weights from a variable font. The instances were made with fontTools:

```
fonttools varLib.instancer 'Outfit[wght].ttf' wght=600 --update-name-table -o Outfit-SemiBold.ttf
```

Quicksand's license reserves the name "Quicksand", so only the upstream files are shipped for it; don't instance or rename them.
