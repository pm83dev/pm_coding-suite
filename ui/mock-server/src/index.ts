import { MockAgentServer } from "./server.js";

const PORT = Number(process.env.PM_MOCK_PORT ?? 8765);
const TOKEN = process.env.PM_MOCK_TOKEN ?? "dev-token";

if (!process.env.PM_MOCK_TOKEN) {
  console.log(`[mock-server] PM_MOCK_TOKEN non impostato: uso il token di default "${TOKEN}"`);
}

const server = new MockAgentServer({
  token: TOKEN,
  serverVersion: "0.1.0-mock",
  approvalTtlMs: Number(process.env.PM_MOCK_APPROVAL_TTL_MS ?? 120_000),
});

server.start("127.0.0.1", PORT).then((port) => {
  console.log(`[mock-server] in ascolto su ws://127.0.0.1:${port} (token: ${TOKEN})`);
});

for (const sig of ["SIGINT", "SIGTERM"] as const) {
  process.on(sig, () => {
    void server.stop().then(() => process.exit(0));
  });
}
