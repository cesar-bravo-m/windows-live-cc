# Windows Live CC

**Windows Live CC** is a Windows app that shows near real time speech captions from captured system audio, with optional translations to Spanish.

This project is part of the **ESLTools** suite of utilities for developers who are non-native English speakers. See also:

- **[eslint-plugin-only-english-identifiers](https://github.com/cesar-bravo-m/eslint-plugin-only-english-identifiers)** — ESLint plugin that enforces English-only names for variables, functions, and object properties.

> **Status: in development.** Setup and usage may change.

---

## Running the app (for now)

1. **API key:** Set your OpenAI API key in code. In `Program.cs`, assign your key to the `OPENAPI_KEY` constant in the `Program` class:
   ```csharp
   public static string OPENAPI_KEY = "sk-your-key-here";
   ```

2. **Run from Visual Studio:** Open the solution in Visual Studio and run the app (F5 or Start), or right click the solution and click "Publish".

---

## License

See repository or project files for license information.
