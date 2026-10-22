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
  plugins: [new HtmlWebpackPlugin({ template: './src/index.html', inject: 'body' })],
  devtool: 'source-map',
  performance: { hints: false },
};
