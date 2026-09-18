## 1. Einmalig auf dem PC (Grundausstattung)
Diese Dinge musst du nur ein einziges Mal installieren. Danach sind sie global auf deinem Rechner verfügbar.

* **.NET SDK 9.0** (oder neuer) von der Microsoft-Website herunterladen & installieren.
* **Visual Studio Code (VS Code)** installieren.
* In VS Code die Erweiterung **"C# Dev Kit"** installieren (unter *Extensions* suchen).

---

## 2. Bei jedem NEUEN Projekt
Wenn du ein komplett neues 3D-Modell in einem frischen Projekt starten willst, folgst du immer diesen 4 Schritten:

1.  **Ordner vorbereiten:** Leeren Ordner erstellen und in VS Code öffnen (`File` > `Open Folder...`).
2.  **Projekt initialisieren:** Terminal in VS Code öffnen (`Terminal` > `New Terminal`) und eingeben:
    ```bash
    dotnet new console
    ```
3.  **PicoGK Engine herunterladen:** Direkt danach im selben Terminal eingeben:
    ```bash
    dotnet add package PicoGK
    ```

## 3. Im Alltag (während du programmierst)
Wenn du neue Dateien hinzufügst (`.cs`), musst du sie nirgends manuell eintragen (achte nur auf den gleichen `namespace`). Um deinen aktuellen Code zu testen, brauchst du immer nur einen einzigen Befehl im Terminal:

```bash
dotnet run
```
im code zum export: // ========================================================
// 5. EXPORT (STL / OBJ)
// ========================================================

// 1. Wandle das Voxel-Volumen in ein Oberflächen-Netz (Mesh) um
Mesh exportMesh = new Mesh(finalModel);

// 2. Exportiere als STL (Der Standard für den 3D-Druck)
exportMesh.SaveToStlFile("C:/Users/DeinName/Desktop/Hirschgeweih.stl");