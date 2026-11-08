# Software von Dritten (Third-Party Notices)

Dieses Projekt benutzt die folgenden Programme und Bibliotheken. Sie gehören ihren jeweiligen Autoren und stehen unter
deren eigenen Lizenzen. Der Code dieses Repositories steht unter der Lizenz in der Datei `LICENSE` (siehe dort).

## Bibliotheken im Programm (über NuGet eingebunden, nicht im Repository enthalten)

| Name | Wofür | Lizenz | Quelle |
| --- | --- | --- | --- |
| **PicoGK** (LEAP 71) | 3D-Voxel-Geometriekern | Apache License 2.0 | <https://github.com/leap71/PicoGK> |
| **Clipper2** (Angus Johnson) | Polygon-Operationen im eigenen 2D-Kernel | Boost Software License 1.0 | <https://www.angusj.com/clipper2/> |

## Externe Programme (werden nur aufgerufen, nicht mitgeliefert)

Das Framework startet diese Programme als eigene Prozesse. Es enthält weder ihren Code, noch linkt es gegen sie, noch liefert es
ihre Programmdateien aus. Jeder installiert sie selbst (siehe `scripts/README.md`).

| Name | Wofür | Lizenz | Quelle |
| --- | --- | --- | --- |
| **SU2** | Strömungslöser (CFD) | LGPL 2.1 | <https://su2code.github.io/> |
| **Gmsh** | Netzgenerator | GPL 2 oder später (mit Ausnahme für bestimmte Pakete) | <https://gmsh.info/> |
| **MPICH** | Parallelrechnen (MPI) | MPICH-Lizenz (BSD-artig) | <https://www.mpich.org/> |
| **ParaView** (optional) | Betrachten der Ergebnisse | BSD 3-Clause | <https://www.paraview.org/> |

## Nur für die Tests (werden nicht ausgeliefert)

xUnit (Apache 2.0), Moq (BSD 3-Clause), coverlet (MIT), Microsoft.NET.Test.Sdk (MIT).

## Was daraus folgt

* **Quellcode veröffentlichen (GitHub):** Es müssen keine fremden Lizenztexte beigelegt werden, weil keine fremden Quellen im Repository liegen.
  Diese Datei nennt die verwendete Software trotzdem, das ist üblich und höflich.
* **Programm als fertige Dateien weitergeben (ZIP mit DLLs):** Dann liegen PicoGK und Clipper2 darin. Dann den Lizenztext der Apache License 2.0
  (PicoGK, samt deren `NOTICE`-Datei, falls vorhanden) und der Boost-Lizenz (Clipper2) beilegen.
* **SU2, Gmsh, MPICH nicht ins Repository kopieren und nicht in das Programm einbinden.** Gmsh ist GPL: Wer seinen Code *in ein Programm
  einbindet*, muss das ganze Programm unter die GPL stellen. Als eigenständiges Programm aufzurufen ist dagegen unproblematisch.
* Die Ergebnisse (Zahlen, Bilder, STL-Dateien) des Programms gehören dem Nutzer; die genannten Lizenzen stellen darauf keine Ansprüche.

_Stand: 09.11.2026. Die Angaben zu den NuGet-Paketen stammen aus deren Paketbeschreibung. Die Lizenzen von SU2, Gmsh, MPICH und ParaView bitte vor der
Veröffentlichung auf den genannten Webseiten gegenprüfen; der Text ist keine Rechtsberatung._
