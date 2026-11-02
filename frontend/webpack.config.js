const fs = require('fs');
const path = require('path');
const { parse } = require('smol-toml');
const HtmlWebpackPlugin = require('html-webpack-plugin');

// the version is the server's, read once at build time: `backend/pyproject.toml`
// is the single source of truth for it and the boot page prints what it built.
// A real TOML parse, not a regex - a `version` key in another table must never
// be the one that ends up on screen
const pyproject = path.resolve(__dirname, '../backend/pyproject.toml');
const version = parse(fs.readFileSync(pyproject, 'utf8')).project?.version;
if (typeof version !== 'string' || !version) {
  throw new Error(`webpack.config.js: no [project] version in ${pyproject}`);
}

module.exports = {
  entry: './src/main.js',
  output: {
    path: path.resolve(__dirname, '../backend/assets'),
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
