const path = require('path');
const HtmlWebpackPlugin = require('html-webpack-plugin');

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
  plugins: [new HtmlWebpackPlugin({ template: './src/index.html', inject: 'body' })],
  devtool: 'source-map',
  performance: { hints: false },
};
