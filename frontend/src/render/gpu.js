// The GL resource owner and the state cache.
//
// Two things live here and nowhere else.  Every buffer, texture and program the
// renderer creates is handed out by this class and kept in one table, so
// `dispose()` exists for the first time and `setMap()` can let go of the
// previous map's textures and road buffer instead of leaking them.  And every
// state change is diffed against the state this class last set, so a pass says
// what it needs (`withState({ blend: true }, ...)`) instead of writing
// enable/restore pairs by hand and hoping the next pass puts them back.
//
// The cache starts at the GL defaults, which is where a fresh context is, so the
// first pass that asks for them issues no calls at all.

const KIND_BUFFER = 'buffer';
const KIND_TEXTURE = 'texture';
const KIND_PROGRAM = 'program';

function compile(gl, type, src, name) {
  const s = gl.createShader(type);
  gl.shaderSource(s, src);
  gl.compileShader(s);
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) {
    const kind = type === gl.VERTEX_SHADER ? 'vertex' : 'fragment';
    throw new Error(`${name}: ${kind} shader: ${gl.getShaderInfoLog(s)}`);
  }
  return s;
}

export class Gpu {
  constructor(gl) {
    this.gl = gl;
    this._res = new Set();      // every handle handed out, for dispose()
    this._kind = new Map();     // handle -> KIND_*
    this._target = new Map();   // buffer -> the target it was created for
    this._attribs = new Map();  // program -> Map(attribute name -> location)
    this._prog = null;          // the program bound right now (useProgram cache)
    this._units = new Map();    // texture unit -> the texture bound on it
    // the GL defaults: depth test off, depth writes on, no blend, LESS, no cull
    this.state = { depth: false, depthWrite: true, blend: false, func: gl.LESS, cull: false };
  }

  _track(handle, kind) {
    this._res.add(handle);
    this._kind.set(handle, kind);
    return handle;
  }

  // ---------------------------------------------------------------- buffers
  // A tracked buffer with no data yet: `retarget`/`sub` fill it, and batch
  // buffers are allocated once and written every frame.
  buffer(target = this.gl.ARRAY_BUFFER, data = null, usage = this.gl.STATIC_DRAW) {
    const gl = this.gl;
    const buf = this._track(gl.createBuffer(), KIND_BUFFER);
    this._target.set(buf, target);
    if (data) {
      gl.bindBuffer(target, buf);
      gl.bufferData(target, data, usage);
    }
    return buf;
  }

  // (re)allocate a buffer's storage - a static mesh, or a whole new size
  retarget(buf, data, usage = this.gl.STATIC_DRAW) {
    const gl = this.gl;
    const target = this._target.get(buf) || gl.ARRAY_BUFFER;
    gl.bindBuffer(target, buf);
    gl.bufferData(target, data, usage);
    return buf;
  }

  // write into a buffer that already has its storage (the instance path)
  sub(buf, data, byteLength) {
    const gl = this.gl;
    const target = this._target.get(buf) || gl.ARRAY_BUFFER;
    gl.bindBuffer(target, buf);
    if (byteLength === undefined) gl.bufferSubData(target, 0, data);
    else gl.bufferSubData(target, 0, data, 0, byteLength);
    return buf;
  }

  // --------------------------------------------------------------- textures
  // Nearest-filtered and clamped to the edge: the ground and the tile map are
  // looked up by integer texel and must never be interpolated or wrapped.
  texture({ w, h, internal, format, type, data = null, filter = null }) {
    const gl = this.gl;
    const tex = this._track(gl.createTexture(), KIND_TEXTURE);
    const f = filter || gl.NEAREST;
    this._bindForEdit(tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, internal, w, h, 0, format, type, data);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, f], [gl.TEXTURE_MAG_FILTER, f],
    [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) {
      gl.texParameteri(gl.TEXTURE_2D, k, v);
    }
    return tex;
  }

  // re-upload a texture's pixels (`repaintTiles` after the debug tree planter)
  texImage(tex, { w, h, internal, format, type, data }) {
    const gl = this.gl;
    this._bindForEdit(tex);
    gl.texImage2D(gl.TEXTURE_2D, 0, internal, w, h, 0, format, type, data);
    return tex;
  }

  // Texel uploads always go through unit 0, and the cache is told so: the
  // terrain pass leaves unit 1 active, and a texture bound behind the cache's
  // back would make the next `bindTexture` a no-op on the wrong object.
  _bindForEdit(tex) {
    const gl = this.gl;
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    this._units.set(0, tex);
  }

  // -------------------------------------------------------------- programs
  // `name` is what a compile error is reported as - the pass's own name, so the
  // message says which file's shader failed instead of dumping the source.
  program(name, vsSrc, fsSrc, uniforms = []) {
    const gl = this.gl;
    const prog = gl.createProgram();
    const vs = compile(gl, gl.VERTEX_SHADER, vsSrc, name);
    const fs = compile(gl, gl.FRAGMENT_SHADER, fsSrc, name);
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.linkProgram(prog);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
      throw new Error(`${name}: link: ${gl.getProgramInfoLog(prog)}`);
    }
    this._track(prog, KIND_PROGRAM);
    return { name, prog, u: this.uniforms(prog, uniforms) };
  }

  uniforms(prog, names) {
    const o = {};
    for (const n of names) o[n] = this.gl.getUniformLocation(prog, n);
    return o;
  }

  // Attribute locations are per program and never move once it is linked, so
  // they are looked up once instead of once per draw call.
  attrib(prog, name) {
    let m = this._attribs.get(prog);
    if (!m) {
      m = new Map();
      this._attribs.set(prog, m);
    }
    let loc = m.get(name);
    if (loc === undefined) {
      loc = this.gl.getAttribLocation(prog, name);
      m.set(name, loc);
    }
    return loc;
  }

  useProgram(p) {
    const prog = p && p.prog !== undefined ? p.prog : p;
    if (this._prog === prog) return;
    this.gl.useProgram(prog);
    this._prog = prog;
  }

  bindTexture(unit, tex) {
    if (this._units.get(unit) === tex) return;
    const gl = this.gl;
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, tex);
    this._units.set(unit, tex);
  }

  // ----------------------------------------------------------------- state
  // Apply only the parts of `spec` that differ from what is set now.  Every key
  // is optional, and an absent key means "leave it alone".
  setState(spec) {
    const gl = this.gl;
    const cur = this.state;
    if (spec.depth !== undefined && spec.depth !== cur.depth) {
      if (spec.depth) gl.enable(gl.DEPTH_TEST);
      else gl.disable(gl.DEPTH_TEST);
      cur.depth = spec.depth;
    }
    if (spec.depthWrite !== undefined && spec.depthWrite !== cur.depthWrite) {
      gl.depthMask(spec.depthWrite);
      cur.depthWrite = spec.depthWrite;
    }
    if (spec.func !== undefined && spec.func !== cur.func) {
      gl.depthFunc(spec.func);
      cur.func = spec.func;
    }
    if (spec.blend !== undefined && spec.blend !== cur.blend) {
      if (spec.blend) {
        gl.enable(gl.BLEND);
        gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
      } else {
        gl.disable(gl.BLEND);
      }
      cur.blend = spec.blend;
    }
    if (spec.cull !== undefined && spec.cull !== cur.cull) {
      if (spec.cull) gl.enable(gl.CULL_FACE);
      else gl.disable(gl.CULL_FACE);
      cur.cull = spec.cull;
    }
  }

  // Run `fn` with `spec` applied and put the state back afterwards, so a pass
  // owns its state for exactly the length of its draw.
  withState(spec, fn) {
    const saved = { ...this.state };
    this.setState(spec);
    try {
      return fn();
    } finally {
      this.setState(saved);
    }
  }

  // -------------------------------------------------------------- lifetime
  release(handle) {
    if (!handle) return;
    const kind = this._kind.get(handle);
    if (!kind) return;
    const gl = this.gl;
    if (kind === KIND_BUFFER) {
      gl.deleteBuffer(handle);
      this._target.delete(handle);
    } else if (kind === KIND_TEXTURE) {
      gl.deleteTexture(handle);
      for (const [unit, tex] of this._units) if (tex === handle) this._units.delete(unit);
    } else {
      gl.deleteProgram(handle);
      if (this._prog === handle) this._prog = null;
      this._attribs.delete(handle);
    }
    this._kind.delete(handle);
    this._res.delete(handle);
  }

  dispose() {
    for (const handle of [...this._res]) this.release(handle);
  }
}
