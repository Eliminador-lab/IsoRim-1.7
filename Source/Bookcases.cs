using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsoRim
{
    // The books a bookcase holds, stood upright on the shelves of its model. They are baked into
    // the same block of the map mesh as the bookcase itself (SectionLayer_IsoRimBodies), so they
    // cost nothing per frame; the block is rebuilt when a book goes in or comes out
    // (Patches.BookcaseContents_Postfix).
    //
    // The shelves and the places along them come from the model's entry (bookShelves, bookSlots,
    // bookRow; the model faces north). Books fill the lowest shelf first, from the left as seen
    // from the front, the way the game lines them up in its own drawing, then the next shelf up.
    // Each book is a small box in the mean colour of the game's own upright sprite of that book.
    public static class Bookcases
    {
        private const float Thick = 0.12f;    // across the shelf
        private const float Deep = 0.22f;     // front to back
        private const float Shortest = 0.24f;
        private const float Tallest = 0.29f;
        private const int Heights = 6;        // steps between the shortest and the tallest
        private const float Sink = 0.01f;     // into the shelf, so the bottom never lies level with its top
        private static readonly Color Pages = new Color(0.86f, 0.83f, 0.74f);   // the top edge, as the game's sprites show it

        private static readonly Model[] boxes = new Model[Heights];
        private static readonly Dictionary<ThingDef, Color> colours = new Dictionary<ThingDef, Color>();

        // How many books were last baked for each bookcase (for the harness check).
        public static readonly Dictionary<int, int> Printed = new Dictionary<int, int>();

        public static bool Shows(ModelEntry entry)
        {
            return entry != null && entry.def.bookShelves != null && entry.def.bookShelves.Count > 0
                && entry.def.bookSlots != null && entry.def.bookSlots.Count > 0;
        }

        // How many books fit on the model's shelves.
        public static int Room(ModelEntry entry)
        {
            if (!Shows(entry))
            {
                return 0;
            }
            int room = 0;
            for (int shelf = 0; shelf < entry.def.bookShelves.Count; shelf++)
            {
                int first;
                room += SlotsOn(entry.def, shelf, out first);
            }
            return room;
        }

        // How many places a shelf has, and where in bookSlots the first of them is.
        private static int SlotsOn(ModelDef def, int shelf, out int first)
        {
            first = 0;
            if (def.bookShelfSlots == null || def.bookShelfSlots.Count == 0)
            {
                return def.bookSlots.Count;
            }
            if (shelf >= def.bookShelfSlots.Count)
            {
                return 0;
            }
            for (int k = 0; k < shelf; k++)
            {
                first += Mathf.Max(0, def.bookShelfSlots[k]);
            }
            return Mathf.Clamp(def.bookSlots.Count - first, 0, Mathf.Max(0, def.bookShelfSlots[shelf]));
        }

        public static void Print(SectionLayer layer, Building_Bookcase bookcase, ModelEntry entry)
        {
            Printed.Remove(bookcase.thingIDNumber);
            if (!Shows(entry))
            {
                return;
            }
            ModelDef def = entry.def;
            IReadOnlyList<Book> books = bookcase.HeldBooks;
            int count = Mathf.Min(books.Count, Room(entry));

            // The same place and turn as the bookcase's own model (ModelBaker.Bake).
            float yaw = bookcase.Rotation.AsAngle + def.rotation + (entry.Swapped(bookcase.def) ? 90f : 0f);
            Quaternion turn = Quaternion.AngleAxis(yaw, Vector3.up);
            Vector3 center = bookcase.DrawPos;
            Vector3 fit = entry.Fit(bookcase.def) * def.scale;
            int printed = 0;
            int shelf = 0;
            int onShelf = 0;
            int first;
            int perShelf = SlotsOn(def, 0, out first);
            for (int i = 0; i < count; i++)
            {
                while (onShelf >= perShelf && shelf < def.bookShelves.Count - 1)
                {
                    shelf++;
                    onShelf = 0;
                    perShelf = SlotsOn(def, shelf, out first);
                }
                int slot = first + onShelf++;
                Book book = books[i];
                if (book == null)
                {
                    continue;
                }
                Model box = Box(Mathf.Min(Heights - 1, (int)(Hashing.Unit(book.thingIDNumber, 5, 61) * Heights)));
                Vector3 at = new Vector3(def.bookSlots[slot] * fit.x, 0f, def.bookRow * fit.z) + def.offset;
                float baseHeight = def.bookShelves[shelf] * fit.y - Sink;
                if (ModelBaker.WritePart(layer, bookcase, box, center, turn, at, baseHeight, ColourOf(book), def.outline))
                {
                    printed++;
                }
            }
            Printed[bookcase.thingIDNumber] = printed;
        }

        private static Model Box(int step)
        {
            if (boxes[step] == null)
            {
                float tall = Mathf.Lerp(Shortest, Tallest, step / (float)(Heights - 1));
                boxes[step] = ObjLoader.Box(new Vector3(-Thick * 0.5f, 0f, -Deep * 0.5f), new Vector3(Thick * 0.5f, tall, Deep * 0.5f), Pages);
            }
            return boxes[step];
        }

        // The cover colour of a kind of book: the mean of the coloured pixels of the game's upright
        // sprite seen from the spine (its north view: the game draws a south-facing bookcase's books
        // with it), leaving out the white page edges, the dark outline and the lighter top band.
        private static Color ColourOf(Book book)
        {
            Color c;
            if (!colours.TryGetValue(book.def, out c))
            {
                c = SpriteColors.MeanFor(book);
                try
                {
                    Graphic g = book.VerticalGraphic;
                    Material mat = g != null ? g.MatNorth : null;
                    Texture tex = mat != null && mat.HasProperty("_MainTex") ? mat.mainTexture : null;
                    Color32[] px = tex != null ? SpritePixels.Sample(tex, Vector2.one, Vector2.zero, SpriteColors.SampleSize) : null;
                    if (px != null)
                    {
                        List<Color32> cover = new List<Color32>(px.Length);
                        for (int i = 0; i < px.Length; i++)
                        {
                            float h, s, v;
                            Color.RGBToHSV(px[i], out h, out s, out v);
                            if (px[i].a >= 128 && v > 0.12f && !(s < 0.15f && v > 0.7f))
                            {
                                cover.Add(px[i]);
                            }
                        }
                        if (cover.Count > 0)
                        {
                            // The sprite shows the top of the book too (paper, a lighter band);
                            // the cover is the darker part.
                            cover.Sort((x, y) => (x.r + x.g + x.b).CompareTo(y.r + y.g + y.b));
                            cover.RemoveRange((cover.Count * 3 + 4) / 5, cover.Count - (cover.Count * 3 + 4) / 5);
                            c = SpriteColors.MeanOf(cover.ToArray());
                            if (mat.HasProperty("_Color"))
                            {
                                c *= mat.color;
                                c.a = 1f;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[IsoRim] Reading a book's sprite failed: " + e, 93611120);
                }
                colours[book.def] = c;
            }
            return c;
        }

        // The cover colours worked out so far, for the harness log.
        public static string ColourList()
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<ThingDef, Color> kv in colours)
            {
                parts.Add(kv.Key.defName + " (" + kv.Value.r.ToString("0.00") + "," + kv.Value.g.ToString("0.00") + "," + kv.Value.b.ToString("0.00") + ")");
            }
            return string.Join(", ", parts);
        }
    }
}
