export const MODELS = [
    { id: "llama-3.1-8b", label: "Llama 3.1 8B (locale)", endpoint: "http://localhost:8080/v1", contextSize: 8192 },
    { id: "llama-3.1-70b", label: "Llama 3.1 70B (locale)", endpoint: "http://localhost:8081/v1", contextSize: 32768 },
    { id: "gpt-4o-mini", label: "GPT-4o mini (API)", endpoint: "https://api.openai.com/v1", contextSize: 128000 },
];
export const DEFAULT_MODEL = MODELS[1].id;
//# sourceMappingURL=types.js.map