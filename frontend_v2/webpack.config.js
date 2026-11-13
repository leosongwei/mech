const fs = require('fs');
const path = require('path');
const HtmlWebpackPlugin = require('html-webpack-plugin');

// The version is the server's, read once at build time: `MechServer.csproj` is
// the single source of truth for it and the boot page prints what it built.  A
// real XML read of the project's own `Version` property, not a regex over the
// whole file - a version string in a `<PackageReference>` must never be the one
// that ends up on screen.
const csproj = path.resolve(__dirname, '../MechServer.csproj');
const project = fs.readFileSync(csproj, 'utf8');
const match = project.match(/<Version>\s*([^<\s]+)\s*<\/Version>/);
if (!match) {
  throw new Error(`webpack.config.js: no <Version> in ${csproj}`);
}
const version = match[1];

// Same output directory as the old client on purpose: `dev.sh` and the server's
// static file handler do not care which tree produced `assets/`, so the new
// client is verified by building it in place and reloading the page.
module.exports = {
  entry: './src/main.js',
  output: {
    path: path.resolve(__dirname, '../assets'),
    filename: 'bundle.js',
    clean: true,
    publicPath: '/static/',
  },
  module: {
    rules: [
      // the Material Symbols subset from src/icons.js: copy it out as-is, the
      // FontFace API in the client loads it at runtime
      { test: /\.woff2$/, type: 'asset/resource', generator: { filename: 'vendor/[name][ext]' } },
    ],
  },
  plugins: [
    new HtmlWebpackPlugin({
      template: './src/index.html',
      inject: 'body',
      templateParameters: { version },
    }),
  ],
  devtool: 'source-map',
  performance: { hints: false },
};
