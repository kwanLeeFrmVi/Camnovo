"""Latency control for the live worker; independent of camera pixels and GPU APIs."""
WIDTHS=(128,192,320,640,960,1280,1920)
DEFAULTS={2:640,3:192,4:640,5:320,6:320,7:192,8:320}

class LiveSizePolicy:
    def __init__(self):
        self.key=None
        self.width=640
        self.slow=0
        self.observations=0

    def choose(self,backend,level,maximum,automatic):
        key=(backend,level,maximum,automatic)
        if key!=self.key:
            self.key=key
            self.width=min(maximum,DEFAULTS[backend]) if automatic else maximum
            self.slow=0
            self.observations=0
        return self.width

    def observe(self,age_ms):
        if not self.key or not self.key[3]:return
        self.observations+=1
        # Exclude the first inference: graph compilation is not steady-state cost.
        if self.observations==1:return
        self.slow=self.slow+1 if age_ms>420 else 0
        if self.slow>=2 and self.width>WIDTHS[0]:
            self.width=WIDTHS[WIDTHS.index(self.width)-1]
            self.slow=0
            self.observations=0
