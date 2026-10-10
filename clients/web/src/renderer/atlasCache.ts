/**
 * Decoded atlas images shared by the part textures and the level's props. Both manifests point at the
 * same files (`IngameAtlas.png` and `IngameAtlas2.png` carry the parts and most of the level's
 * decorations), and a 2048-square atlas decodes to 16 MB, so a second copy is worth avoiding. Keyed by
 * file name: the URLs differ only by their base directory, which never varies in the app.
 */
export const sharedAtlasImages = new Map<string, CanvasImageSource>();
