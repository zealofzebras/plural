import unittest

from store_assets import render_logo


class SharedLogoTests(unittest.TestCase):
    def test_shared_brand_colors(self):
        image = render_logo(128)
        self.assertEqual(image.getpixel((64, 110)), (24, 35, 33, 255))
        self.assertEqual(image.getpixel((36, 40)), (244, 247, 245, 255))
        self.assertEqual(image.getpixel((60, 40)), (244, 247, 245, 255))
        self.assertEqual(image.getpixel((84, 40)), (238, 75, 85, 255))

    def test_transparent_corners_and_export_sizes(self):
        for size in (16, 32, 128, 1024):
            with self.subTest(size=size):
                image = render_logo(size)
                self.assertEqual(image.size, (size, size))
                self.assertEqual(image.mode, "RGBA")
                self.assertEqual(image.getpixel((0, 0))[3], 0)
                self.assertEqual(image.getpixel((size // 2, size // 2))[3], 255)

    def test_monochrome_mark_has_no_background_tile(self):
        image = render_logo(128, (255, 255, 255))
        self.assertEqual(image.getpixel((64, 110))[3], 0)
        self.assertEqual(image.getpixel((48, 40))[3], 0)
        for point in ((36, 40), (60, 40), (84, 40)):
            self.assertEqual(image.getpixel(point), (255, 255, 255, 255))


if __name__ == "__main__":
    unittest.main()