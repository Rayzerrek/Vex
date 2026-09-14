import { defineConfig, lazyPlugins } from "vite-plus";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

export default defineConfig({
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    include: ["src/**/*.{test,spec}.{ts,tsx}"]
  },
  fmt: {
    trailingComma: "none",
    printWidth: 80,
    experimentalSortPackageJson: false
  },
  lint: {
    plugins: ["react", "typescript", "oxc"],
    categories: {
      correctness: "error"
    },
    rules: {
      "no-explicit-any": "error",
      "no-unused-expressions": "off",
      "no-this-alias": "off",
      "react-hooks/exhaustive-deps": "warn",
      "no-unused-vars": [
        "error",
        {
          argsIgnorePattern: "^_",
          varsIgnorePattern: "^_",
          caughtErrorsIgnorePattern: "^_"
        }
      ],
      "react/rules-of-hooks": "error",
      "react/only-export-components": [
        "warn",
        {
          allowConstantExport: true
        }
      ],
      "vite-plus/prefer-vite-plus-imports": "error"
    },
    options: {
      typeAware: true,
      typeCheck: true
    },
    ignorePatterns: ["dist/**", "**/env.d.ts"],
    jsPlugins: [
      {
        name: "vite-plus",
        specifier: "vite-plus/oxlint-plugin"
      }
    ]
  },
  plugins: lazyPlugins(() => [react(), tailwindcss()])
});
