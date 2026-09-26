import { mkdir, readFile, writeFile, copyFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import path from "node:path";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const source = path.join(root,"site");
const output = path.join(root,"artifacts","site");
let release = JSON.parse(await readFile(path.join(source,"release.json"),"utf8"));
if(process.argv.includes("--refresh-release")){
  try{
    const response = await fetch("https://api.github.com/repos/franklai-rise/Pinboard/releases/latest",{
      headers:{Accept:"application/vnd.github+json",...(process.env.GITHUB_TOKEN ? {Authorization:`Bearer ${process.env.GITHUB_TOKEN}`} : {})},
      signal:AbortSignal.timeout(10000)
    });
    if(!response.ok) throw new Error(`Release metadata: HTTP ${response.status}`);
    const latest = await response.json();
    const asset = latest.assets?.find(item=>item.name==="Pinboard-windows-x64.zip" && item.state==="uploaded");
    if(!/^v\d+\.\d+\.\d+(?:[-.][\w.-]+)?$/.test(latest.tag_name) || !asset || !/^sha256:[a-f0-9]{64}$/.test(asset.digest)) throw new Error("Unverified release metadata");
    release={version:latest.tag_name,bytes:asset.size,publishedAt:latest.published_at,sha256:asset.digest.slice(7)};
  }catch(error){console.warn(`Using checked-in release metadata: ${error.message}`);}
}
if(!/^v\d+\.\d+\.\d+(?:[-.][\w.-]+)?$/.test(release.version) || !Number.isSafeInteger(release.bytes) || release.bytes <= 0 || !/^[a-f0-9]{64}$/.test(release.sha256)) throw new Error("Invalid release snapshot");
await mkdir(path.join(output,"assets"),{recursive:true});
for(const name of ["index.html","styles.css","app.js"]){
  const text=await readFile(path.join(source,name),"utf8");
  await writeFile(path.join(output,name),text.replaceAll("__VERSION__",release.version).replaceAll("__SIZE__",(release.bytes/1024/1024).toFixed(1)).replaceAll("__SHA256__",release.sha256));
}
for(const name of ["pinboard-logo.png","screenshot-inbox.png","text-library.png","compact-library.png"])
  await copyFile(path.join(root,"docs","images",name),path.join(output,"assets",name));
await writeFile(path.join(output,".nojekyll"),"");
await writeFile(path.join(output,"release.json"),JSON.stringify(release,null,2));
await writeFile(path.join(output,"robots.txt"),"User-agent: *\nAllow: /\nSitemap: https://franklai.com/Pinboard/sitemap.xml\n");
await writeFile(path.join(output,"sitemap.xml"),'<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://franklai.com/Pinboard/</loc></url></urlset>\n');
console.log(`Pinboard website built: ${path.relative(root,output)} (${release.version})`);
