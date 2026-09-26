import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),"../artifacts/site");
const types={".html":"text/html; charset=utf-8",".js":"text/javascript; charset=utf-8",".css":"text/css; charset=utf-8",".png":"image/png",".json":"application/json",".xml":"application/xml",".txt":"text/plain"};
const server=createServer(async(req,res)=>{
  try{
    let pathname=decodeURIComponent(new URL(req.url,"http://localhost").pathname);
    if(pathname.endsWith("/")) pathname+="index.html";
    const file=path.resolve(root,"."+pathname);
    if(!file.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
    const content=await readFile(file);
    res.writeHead(200,{"Content-Type":types[path.extname(file)]||"application/octet-stream","Cache-Control":"no-store","X-Content-Type-Options":"nosniff"});res.end(content);
  }catch{res.writeHead(404);res.end("Not found");}
});
server.listen(4176,"127.0.0.1",()=>console.log("Pinboard preview: http://127.0.0.1:4176/"));
