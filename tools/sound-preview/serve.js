// Mini serveur statique (racine = le repo) pour écouter les sons du jeu depuis evo-reveal.html :
//   node tools/sound-preview/serve.js   puis   http://localhost:8765/tools/sound-preview/evo-reveal.html
const http = require("http");
const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..", "..");
const types = { ".html": "text/html; charset=utf-8", ".wav": "audio/wav", ".js": "text/javascript" };

http.createServer((req, res) => {
  const file = path.join(root, decodeURIComponent(req.url.split("?")[0]));
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    res.writeHead(404); res.end(); return;
  }
  res.writeHead(200, { "Content-Type": types[path.extname(file).toLowerCase()] || "application/octet-stream" });
  fs.createReadStream(file).pipe(res);
}).listen(8765, "127.0.0.1", () => console.log("http://localhost:8765/tools/sound-preview/evo-reveal.html"));
