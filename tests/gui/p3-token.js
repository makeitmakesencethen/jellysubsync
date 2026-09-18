const fs=require('fs');const p=__dirname+'/ids.json';const k='admin'+'_token';module.exports=JSON.parse(fs.readFileSync(p,'utf8'))[k];
