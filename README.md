# MroczwareTools

Rozszerzenie (VSIX) do **Visual Studio 2022**, które przyspiesza codzienną pracę w projektach .NET
z warstwą danych opartą na SQL (Dapper / ręcznie pisane repozytoria). Generuje boilerplate z SQL
i klas C# oraz dodaje szybkie refaktoryzacje Roslyn.

## Funkcje

Menu **Mroczware** (Tools / menu kontekstowe):

| Komenda | Co robi |
|---|---|
| Wygeneruj DTO/Model | Tworzy klasę DTO/modelu na podstawie tabeli lub zapytania SQL |
| Wygeneruj na podstawie SQL | Generuje repozytorium z zapytań `.sql` |
| Wygeneruj odpowiedź z SQL | Generuje klasę odpowiedzi (response) z wyniku zapytania |
| Wygeneruj SQL | Buduje skrypty SQL na podstawie modelu |
| Dodaj pusty serwis / repozytorium | Szkielety serwisu i repozytorium zgodne z konwencją projektu |
| Generuj test integracyjny | Szkielet testu integracyjnego |
| ResetIIS | Uruchamia `iisreset` bezpośrednio z IDE |
| View Logs | Szybki podgląd plików `error*.log` z folderu logów aplikacji |

**Quick Actions (Ctrl + .)** – refaktoryzacje Roslyn:

- generowanie mapperów (metoda i konstruktor),
- generowanie klasy parametrów (`Params`),
- dodawanie `IDependency` do klasy,
- inicjalizacja pól/właściwości,
- przeniesienie kodu do klasy `partial`.

## Wymagania

- Visual Studio 2022 (17.x), Windows x64
- .NET Framework 4.8 (budowanie)
- Workload „Visual Studio extension development”

## Budowanie i instalacja

1. Otwórz `MroczwareTools.sln` w Visual Studio 2022.
2. Zbuduj projekt (`Ctrl + Shift + B`) – plik `.vsix` pojawi się w `bin\Debug` / `bin\Release`.
3. Uruchom `.vsix` i zrestartuj Visual Studio, albo debuguj (`F5`) w eksperymentalnej instancji VS.

## Technologie

Community.VisualStudio.Toolkit, Roslyn (`Microsoft.CodeAnalysis`), `Microsoft.SqlServer.TransactSql.ScriptDom`,
AvalonEdit, WPF.

## Status

Projekt osobisty / edukacyjny – część większego zestawu narzędzi Mroczware. Bez gwarancji.
