import subprocess, json, sys, base64, os, time
PORT = os.environ.get("PORT", "8766")
class C:
    def __init__(self):
        self.p = subprocess.Popen(["ssh","-o","BatchMode=yes","-W",f"127.0.0.1:{PORT}","win-vm"],stdin=subprocess.PIPE,stdout=subprocess.PIPE)
        self.n = 0
        self.rpc("initialize",{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"1"}})
        self.send({"jsonrpc":"2.0","method":"notifications/initialized"})
    def send(self,o):
        self.p.stdin.write((json.dumps(o)+"\n").encode()); self.p.stdin.flush()
    def rpc(self,m,params=None):
        self.n+=1; self.send({"jsonrpc":"2.0","id":self.n,"method":m,"params":params or {}})
        line=self.p.stdout.readline()
        return json.loads(line)
    def call(self,name,args=None,show=True):
        r=self.rpc("tools/call",{"name":name,"arguments":args or {}})
        res=r.get("result") or r
        out=[]
        for c in res.get("content",[]):
            if c["type"]=="text": out.append(c["text"])
            elif c["type"]=="image":
                ext="jpg" if "jpeg" in c["mimeType"] else "png"
                fn=f"/tmp/winmcp/out/img_{int(time.time()*1000)}.{ext}"
                open(fn,"wb").write(base64.b64decode(c["data"])); out.append(f"[IMAGE {c['mimeType']} {len(c['data'])} b64 chars -> {fn}]")
        txt="\n".join(out)+(" (isError)" if res.get("isError") else "")
        if show: print(f"--- {name} {json.dumps(args or {})}\n{txt}")
        return txt
    def close(self):
        try: self.p.stdin.close()
        except: pass
        self.p.wait(timeout=10)
if __name__=="__main__":
    c=C()
    for a in sys.argv[1:]:
        d=json.loads(a); c.call(d.pop("tool"),d)
    c.close()
