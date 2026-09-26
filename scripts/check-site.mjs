import assert from "node:assert/strict";
import { readFile, readdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),"..");
const site=path.join(root,"artifacts","site");
const html=await readFile(path.join(site,"index.html"),"utf8");
const script=await readFile(path.join(site,"app.js"),"utf8");
const allowed=new Set(["index.html","styles.css","app.js",".nojekyll","release.json","robots.txt","sitemap.xml","assets/pinboard-logo.png","assets/screenshot-inbox.png","assets/text-library.png","assets/compact-library.png"]);
const files=[];
async function walk(dir){for(const entry of await readdir(dir,{withFileTypes:true})){assert(!entry.isSymbolicLink(),"No symlinks in the deployment");const name=path.join(dir,entry.name);if(entry.isDirectory())await walk(name);else files.push(path.relative(site,name).split(path.sep).join("/"));}}
await walk(site);
assert.deepEqual(new Set(files),allowed,"Publish only the explicit website allowlist");
assert(!/__VERSION__|__SIZE__|__SHA256__/.test(html),"Release placeholders must be resolved");
const ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(match=>match[1]);
assert.equal(ids.length,new Set(ids).size,"HTML IDs must be unique");
for(const match of html.matchAll(/(?:src|href)="([^"]+)"/g)){
  const link=match[1];
  if(link.startsWith("#")){assert(link==="#"||ids.includes(link.slice(1)),`Missing section ${link}`);continue;}
  if(link.startsWith("https://")){
    const url=new URL(link);
    if(url.hostname==="github.com") assert(url.pathname.startsWith("/franklai-rise/Pinboard"),"Every GitHub link must belong to Pinboard");
    else assert.equal(link,"https://franklai.com/Pinboard/","Only the canonical site may use another hostname");
  }else{assert(link.startsWith("./"),"Project site assets must use relative URLs");assert((await stat(path.join(site,link))).isFile(),`Missing asset ${link}`);}
}
const downloads=[...html.matchAll(/href="([^"]*\/releases\/latest\/download\/[^"]+)"/g)].map(match=>match[1]);
assert.equal(downloads.length,3,"Two ZIP buttons and one checksum link");
assert(downloads.every(link=>/^https:\/\/github\.com\/franklai-rise\/Pinboard\/releases\/latest\/download\/Pinboard-windows-x64\.zip(?:\.sha256)?$/.test(link)),"Downloads must target this product's own release");
assert(!/navigator\.clipboard|sendBeacon|google-analytics|googletagmanager|fetch\(/.test(script),"The demo must not read clipboard contents or use remote services");
for(const name of files.filter(name=>/\.(html|css|js|json)$/.test(name))){const text=await readFile(path.join(site,name),"utf8");assert(!/\b[A-Za-z]:[\\/]|BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY|github_pat_[A-Za-z0-9_]{30,}/.test(text),`Private data pattern in ${name}`);}
execFileSync(process.execPath,["--check",path.join(site,"app.js")],{stdio:"pipe"});
console.log(`Website checks passed: ${files.length} public files, local assets, valid anchors, Pinboard-only downloads, no clipboard/network APIs.`);
