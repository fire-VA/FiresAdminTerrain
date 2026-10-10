# Third-party notices — FiresAdminTerrain

## FastNoiseLite 1.0.1 — MIT

`Modules/BcImport/FastNoiseLite.cs`

Copyright (c) 2020 Jordan Peck (jordan.me2@gmail.com) and contributors.
<https://github.com/Auburn/FastNoiseLite>

Licensed under the MIT licence; the full licence text is at the top of that file, unmodified. The only change is
a namespace wrapper (`VerdantsAscent.Terrain.BcImport`) so the type does not sit in the global namespace.

**Why this exact copy.** FAT reproduces BetterContinents' terrain for worlds that were generated with it, and
BC's noise comes from the copy of FastNoiseLite that BC itself vendors
(`JereKuusela/BetterContinents`, `FastNoiseLite/FastNoiseLite.cs`, version **1.0.1**). FastNoiseLite's own master
is **1.1.1** and differs in 131 lines, so it would not reproduce BC's output. Verified: with BC's 1.0.1 copy,
FAT's noise matches BC's own DLL bit for bit over 20,000 samples; before the swap, 16,626 of 20,000 differed in
their last bits.

MIT permits this use with the notice preserved. Nothing from BetterContinents itself is included — FAT's
generator is written from BC's published source, not copied from it.
