# cards.dll

The card drawing Windows' Solitaire (sol.exe) and FreeCell (freecell.exe)
ask cards.dll for, written in C# and drawn with GDI alone: the six exports,
their arguments and their stdcall as Windows 2000's cards.dll has them.

    corc compile --dll cards.cor -o cards.dll

Put it beside sol.exe or freecell.exe. Under Wine, which has a cards.dll of
its own, say which one to load: `WINEDLLOVERRIDES=cards=n wine sol.exe`.
FreeCell's game 1 deals as the published game 1 does, which is how the card
numbering (rank * 4 + suit; clubs, diamonds, hearts, spades) was checked.
