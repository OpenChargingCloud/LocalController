'use strict';

const path                  = require('path');
const HtmlWebpackPlugin     = require('html-webpack-plugin');
const MiniCssExtractPlugin  = require('mini-css-extract-plugin');
const TerserPlugin          = require('terser-webpack-plugin');

const appVersion            = require('./package.json').version;

// Everything webpack emits lands in dist/ and is embedded into the C# assembly
// by ../LocalController.csproj (target "EmbedFrontend"):
//
//   dist/index.html                     the SPA stub, served for every page URL
//   dist/favicon.svg
//   dist/assets/main.<contenthash>.js   the web interface
//   dist/assets/main.<contenthash>.css  its stylesheet
//   dist/assets/*                       fonts, images, source maps
//
// One entry point, because a local controller has one door: everything it can
// be told is administration, behind the same sign-in, on the same port. A
// charging station has a second bundle for the screen on its front; a box in a
// cabinet has nobody standing in front of it.
//
// Directory names below dist/ must not contain dots: the server maps the URL
// path "assets/app.1234.js" onto the manifest resource name
// "<prefix>assets.app.1234.js", so a dot in a directory name would be ambiguous.

module.exports = (env, argv) => {

    const isProduction = argv.mode === 'production';

    return {

        entry: {
            main: './src/main.ts'
        },
        target:  ['web', 'es2022'],

        // No eval-based devtool: the page is served with a strict
        // Content-Security-Policy that forbids eval().
        devtool: isProduction ? 'source-map' : 'cheap-module-source-map',

        output: {
            path:                 path.resolve(__dirname, 'dist'),
            filename:             'assets/[name].[contenthash].js',
            assetModuleFilename:  'assets/[name].[contenthash][ext]',
            // Relative, and the <base href> in index.html is what they resolve
            // against - so a deep page URL like /logs still finds the bundle,
            // and so does the same bundle mounted below /EV or /CSMS. An
            // absolute '/' worked only at the root.
            publicPath:           'auto',
            clean:                true
        },

        resolve: {
            extensions: ['.ts', '.js']
        },

        module: {
            rules: [
                {
                    test:     /\.ts$/,
                    use:      'ts-loader',
                    exclude:  /node_modules/
                },
                {
                    test:     /\.s?css$/,
                    use:      [MiniCssExtractPlugin.loader, 'css-loader', 'sass-loader']
                },
                {
                    test:     /\.(woff2?|ttf|eot|svg|png|jpe?g|gif|webp)$/,
                    type:     'asset/resource'
                }
            ]
        },

        plugins: [
            new MiniCssExtractPlugin({
                filename: 'assets/[name].[contenthash].css'
            }),
            new HtmlWebpackPlugin({
                template:  './src/index.html',
                filename:  'index.html',
                chunks:    ['main'],
                favicon:   './src/favicon.svg',
                title:     'Local Controller',
                version:   appVersion
            })
        ],

        optimization: {
            minimizer: [
                new TerserPlugin({
                    // Keep the /*! ... */ license banners of the bundled
                    // libraries inside the bundle instead of emitting a
                    // separate .LICENSE.txt - which would be one more file to
                    // embed and one more URL to serve, for a comment.
                    extractComments: false,
                    terserOptions: { format: { comments: /^\**!/ } }
                })
            ]
        },

        performance: {
            hints: false
        }

    };

};
