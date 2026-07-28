import js from "@eslint/js";
import { defineConfig } from "eslint/config";
import globals from "globals";

export default defineConfig([
  {
    files: ["src/ChairSide.Board/wwwroot/**/*.js"],
    extends: [js.configs.recommended],
    languageOptions: {
      ecmaVersion: "latest",
      sourceType: "module",
      globals: {
        ...globals.browser,
        signalR: "readonly"
      }
    },
    linterOptions: {
      reportUnusedDisableDirectives: "error"
    }
  }
]);
