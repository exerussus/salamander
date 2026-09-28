using UnityEngine;

namespace Dsl.Unity.Editor
{
    /// <summary>
    /// Значок .sal в окне Project. PNG вшит в код, а не лежит рядом файлом:
    /// Dsl.Unity копируют в проекты папкой, и путь или GUID картинки в каждом
    /// проекте свой, а строка в коде едет вместе с импортёром без настроек.
    ///
    /// СГЕНЕРИРОВАНО Tools/icons/build.py из Tools/icons/salamander.svg —
    /// руками не править: значок меняется в генераторе.
    /// </summary>
    internal static class SalamanderIcon
    {
        private const string PngBase64 =
            "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAQAElEQVR4nOxbCZAc5XV+fcxMz7UzO3tqtaf2RBISAoFkEJeIUQgx" +
            "GJzEdlJxggsnFWJXnLKBUlIQygGjRYBIwAFM4lBJMCkOYXFJxhISAsSiRRLS6t5be8zOzM599N2d93fPzM5gAZplVxalPKmrj/n/" +
            "7v9//zu+996/LJznxMJ5Tuc9A2j4ktLGGnA+2Gprgy9IFHwJaUMHt8bC0O847RY6kRKP4ipuSJ0Unr8PQIESiYEvAT3UZmv9qo/1" +
            "/SaiRjd2WpeBTj33B6ubKtcsrYWUoFZZrMwtvF2v3B5W3oQS6ZyXgIc67Ts8LttaWVFBlDWtymunfW4rXNZZBXYrC5qmQ4qX4dnt" +
            "J8L/2C8txi7BUt5/ThvB7nbrPctaK9auubDWuNf1rM3CCyD/8RyM8RCKC2ClWa8bpPZkiQw4p40gRdM/uvSCauOazNkQV21m8hT+" +
            "q/FwsLSxHNxOC3N9DXsllEjnLAM2dthvaanzeGwsbUyekJ69CER5OH4qBqqqgYoMeX73IISS4tjLAeUA/mwr4TPnrgroFPVXS5t9" +
            "+ZXWcaLTCRFe2j2kJRS9j6OhIZ6RfVVlNvI8dE+/8D3spuEhlfKdc4IBD3c4LtVpzZl/oGvVLgezrr7KfJRb+Y9OBuFgQtn4P5PS" +
            "zlY7eONy4DbkFOyOKi/hzyppAoaCnDn9ThiwocOynKLY63Flr9Ep/aryMs7FWRmgUMkryjiIpURoq/MYOk9WnlBakGFoKiFNCxox" +
            "ckODPPQ/NCz9Bq8vAlOVj+ERgxLprLnBhzugUgPHN3Godzg4dnFjjRsaa1xAVplMPr9uuNp6wXXO4MVTEvSNRuFUMAXxtDSEI9+k" +
            "gf76+uPCCHwBOisM6O6y/8hCM/e2NpSVrWivhAqPHSelmbpt6DgY/rzwPrfyeRuga8ZwyXNkABwYCsPgVAoURXmSpqH7x8eEUZgF" +
            "zSsDutvttyLWfGRFR1XzqiU1wDKUudKFVt24z00WIH+hz+h+rg+5tyyyAbfaA2pIgtjWMBwaisKxiThkBOmeu04I90OJNC8MePgC" +
            "rknV6Kfra1zrrl5RB5WtTmBabaALGshHMwA8zKw8wwLj4NAaWYB2WYHiiDrYUaupvESgrwONF0BLpYG7jgbaY5ou/q0ISCd5kBUN" +
            "evqn4ch4rJ9S4Tt3D/A9ZzrWOWfAhi775TRQL69dWV+7uKXcmID96+VAucywQzmWAfGABEyZG2i3Cyib6bbpGhGYpowxcXWSAuUI" +
            "awoKAUAsi+2sxg1Th+0aBNDTAMktEdATQl5iJhEf7DwagERG/tP1/fzzZzLeOQ2GNnTYv+XkLG/+4ZXNrkULy0zuErFdYgfKkkWx" +
            "ogUYdgFQDnyGq67xPKjTUWAXy0DZzeHQDh2EHj+KeRzUSBy0cBSU4DSo8SS2RWnwO0GLuIAtR8ai9OiCBLokg5uzQGOFgzDiG1eU" +
            "UXU7wsrrnzfmOWPAxk7HzXaOfekb17VBTbndNGSGDuOVgitXYcWBYvAScIGWlEENh0E+NQFaLAE6MoGpRhXwWUwmoapIH6Xy8Ddv" +
            "KzAg0tM8KMgQNRYnUBkYbxmwFeVA2znQMjzYsENHrRtiafmSpTaN3hVRdsF8M2BDO3cVy9JbbrlmEVuB2DxvxdGx62SQzgW4im7Q" +
            "ULTlkQCogRAOVpjB9mRuYZQANPRaXAGxJ4X6rhYwEYqZASYztDh6gUgCJ29DlUKJqEbkKGM/ZEQzSkIgLly9lIOBd6NKH8wXAx5s" +
            "5xYxDL3jhsub3PXVqNPGYpmDpKxWsDQ1GGctlTJWHASx2NpnJ5WOyzB2KAY9b03A3o8DUOHmwMWxeQ+wH93ezsNTMBHhDTdII3Od" +
            "GA4TRqjhGEqNCJZ2K7ALHfgtZFVKgFoEVSenkmtrOfm5viQkTjf+L4wEaZr6+aqlNb6WurL8ZIy1slqAbazHBgxooWmQQ+GsVYci" +
            "Pz8ZTkPfcBQG/AkxLOk7w7J+Eo18U1MwdXOtdwYvNFY64d2T0wOvjWeeWuSgly/gmBuqXbbKLvzuhfUelLQMWNCOENIylaAmePDi" +
            "Ny5u8HinB7V/Rs7fdrrxfyEJeKjL/oOGGvffXreyvkg8Kc4OloaFxrUyjsYsGs/+nu2IF8OBJGzfPwk7j4f2vD6WefTpMfHxPTGl" +
            "50BC7S9j4MhCK/WtJQ3evCt02FgYmUr43JTu1jVtoMxKr7FbGAZXWB6dTjM+9BK+C91oDHVQxpD5bi8oaFyrHFYYCSW7VEF+cViC" +
            "8JwxYMMirtHGWd76+jWLwEpC1pzOswxYl9cAU486bUUrnuJBT2pF8fzuvinYfTgQeifAb/j5mPT8YEZD3TAGR/D84UFeP77GTd3W" +
            "udDjtTJ0nmkk7q/3cA3lHLt6yUIvc1VnNYyF08xgJPNhvz9pVQ6knOX9KAUZBQ0jMs+CrhSNLC+rbEZWvT0x5ZU5Y8D11dZHL+mq" +
            "XtG8wA0z0A4QqdWCZbmIfl8FupxBG2ADqS9j/JbKyPDK+yPQOxZ//f5BYf2xtDaJvYbx2IfHFBgQyaSv+phOt926kiQ8ZpAhgAdX" +
            "lLi6CpcNFFmDOvQ4sbRUvz+UWc8L8sULymwul47fdtmB9XoM11mGnB8KJuveDivP4auThfOYVULkkS5bB7q8717cVZUdnCmmNIIb" +
            "ZoGl+K3o/9kGK8SSIry4ewj2+DOPPzIsPgFm5PYeHidP9w0UpE0E72cEkuiljMSHyeSs4cR7AQ0g8f1ODKZEiq4cSMp//PaJoJgU" +
            "0ZOMThrtrE11UEbwgdde9Se1lls++Z1ZScDvVVrXX9RRdXlDTTaEN6SfTLQOdZACBlEdQXQG4Sol3o7Bq3tGYW+If+w/J6St+JSI" +
            "PMneyJ/2jbem1fAqD+M6PBZZPhRIWT9AqDsV50FAN+e1I5jC9xP1iKBH6BkMS8+MS4/2JrTBZW56kOflGzvKHZgbQpiNmUIV1UDB" +
            "cHoyzmsfxNQthd/9TC/Q3WavB0Zfh4ijofA5rvYdy1p9ANkojRgqutIHFOJ6acwPWhpd0jIcABok6VAGtn80Ab1B/t+enZC3Yacx" +
            "PI7CGdB9/fydeNqMFrysxophQIVy2dJw5m+8dmt5A4r+FCZDVQ3jC0wWZVRjUiMbh8Qn72eoOwamU0vbXYgwF3jA+71a6NzFgGco" +
            "vBTb1ICpdp/NgI1d3DUobTubF3qhupzLPjVFvQz10I7chVwIi6vB+HygiaJhdOQosnhQNMT1MLq4volk7zNj4qtg6vkZTb6APsSj" +
            "MiCB65d+ad/ttCW0ZyC0qb3aDftGIqoMeujjuPI0mBkhQz9wSW7fNxbrafU5EHpLhnFGjwEeK9PczEHjiHAGDMC53b3mogWwojOX" +
            "lS2M06mieJ7xeI3JGoCkwM+TfP2eowFx85T0L/gohcdBKJ1IIiCYPeDfJ+THUPVjJ6LijVuC4gvDvAFwyKCGch3u7ec/fKADekaj" +
            "/OqWE36QxziQxmNQjqrT6rQsGRHkdz6XAajEX+lsKofs7IthKDF4XtSvGhYDFgVop2kL1Hg8zyxC7x8JQH9K+a+jaXUab/tgjujZ" +
            "KZlEeofxIKI5jsfIJ9tYaf3xkUhmdaMnhQFV0hg/Zs6hxkoRgILxtulxTsuAjV2Or1V4OY8DrWfRylPmyjM1FuDWefPt1QEG5OE0" +
            "Ynk9j/ByFRsvAx1tDggNZE4PRWdJqF9GAvRTiRWEN0/F2Jnx49mGroVjKOK30UCZDChyg8S9bWizP4KT/AXG83n/K6MlP4XIbWAs" +
            "BuOBFLCtxal3ukI2khV6AbYnTuDWK5ph7QVVV9/eYH+lu9OxEs4i/f0IxERVOxBGL5GLJklCCg8Sclpz7fIS8NM2qGItluOXLaum" +
            "SOOqcjtkZwOvvjcMx/2pPfiYvrKzcnXTgBfYNnv+Y2oIJSWZLqrY5HJ4qzqqoKHC2bj948neB9vtZ5yomAvCwGx/XJBXVDow74Dj" +
            "IsKJBwEWefeflwCasd/WXOehEN3BJQhwcmJzeCgCJ/zJQw8MCj9JKtDvRg+g+mXgt2KSoh/TUQcZ0CPIAEkqkgADHGmm+NX57PBH" +
            "lzeB1Ur/8sctWN09WwygtCECishakHGIaD0lTc8UtskzYDrG/2wILWUkYaLRnLXP4AuwERGbdD1HRS2MCXAIE8R3k6BHbUY2Jrfy" +
            "hMVTkQyQam6h4SSV3GaM6OIy9V04S/UIXaNjkqqZ1zgMHlU5qmjEIKvwSQZUe21XYjoLfC4OwjHBeCYj6rqorQLsLNWCt2GKojiW" +
            "+UStDg2LpihFGaBToTS88N6QEbcbDiq7AhPhDAzz2iBeVsFZIMzFqHpBdjmBi3kkqZD0uZhvk7u464S4LZwQ9j3x4iEY8SeMTtt7" +
            "x+G1d0cgpWgfg4GgdFnLinWuSouYFChVLxJ/IjWnYtK7vQPhmY9nJEjKanR7WD5ZOID5JAybeYuRXUYQgmMKC7IfcQPx1alcmyJR" +
            "XN8vrFzmZL6t6P6n9vRNuURVn9yflJ7eGtBItIZ5aT0oSEqxrqOI6aSmpc8UNQgDeE0PkMpuDixFsbKTlnWCBYgRisDZIIq6lfh+" +
            "8v3JpKDFZG0/mMVT6bQMIHQorb5w6KRKfOyi7CMyWxJaDeI826JJqdjaE11Hu8C2Y3Y2poCCmFVBiaiyMNcRe5FjlojqhCn/hgoL" +
            "JMMynC2q9NrMtNp4QqSH0iqB1UXMP50xIgaiH49TeJRlO5hGQ6P6oymxCFzoqP+WToTDDSYwSr8cMs7XXlhT3lLlMt+I7eornLCy" +
            "ydfydxB54t5+4TI4C8QwlE4yQpgMgZG4EN8cVPbi4+nCNp+VDyB6SmaTt5hpmT8Yxhp9vmIDJM8vYiQ4szmLbeGMDFErJkithsE0" +
            "DaMNpeHSRT6EqEAisg6YZ+ru4v5igYtbQ75/bDoNfknbmv0pUNiupITIfSMYjivqHn8kk/fzOi+C3M9hHk7CslcaxN4kuFHsnt05" +
            "YISqKqoD2clB4vbX9o9DTNLJKtTDvBP1+61YhCWu72AomXkjIPwKzLihaCtd6f6Ygs1Yp1+NqSoj5aEiAmST1SAOos+ZjBsrXodh" +
            "KCYrMdObRPFTjARGHPN0w1G+b9Ow2I3d0jCP1N1hv8xlYb/WjAzYMRqGUxnt+UHeyED9VgW55JQY2vyXh6dSdN4GoASoWKAAPJN6" +
            "AHEITdVO+MtrWw04PYmYYseRgLizf3rfi1PKg6hXBIkdgvkkGv57ZW2Z83AoCSei/NEnx8QXwbRpqd9uWiKRDQmxjNQ7GiTvQjVA" +
            "8ZYGx4xyVS5xmdvTU42FiZswqMJ4wIbmwDGSVklMTzAFD/NERPdrHLYO4v4OBJOpV4MykThiz06crv2scoLryllVUvWbW6ucpjeA" +
            "wjIzVfwAz6TAkUzJVZ1WNY21us0wT9TdZf9+tZ17bHWdx/Lm8LS0LSjfibUGkoIj+cfM6frMDpMz1E1VmJamcniA5AkgiwvAvDdF" +
            "wYwNyPnqJdUQTIjf8/zGcAAAAq5JREFUQTx1F5xGFL8IPdgI5YzD8cNqh/XeFZgq+/VIGD4IS/djdXgATHX7VOBVsgrcVwcOzMnf" +
            "tLzRW7SNJZcvNNQgC5O3TemwvjcGbwwl0A1iwYSlnNf6mHUwx8TYHW93+hz3LkPcsXs8Cj0R6adZn09gt/+z+pbMAIeLu8SKEC/X" +
            "MY8H0OXltrsQ6T8WEeDURbeC54qb4R96gjCG+IHYhEYHsxrmemMGpQ9n0N1tG4lEfh0U7vpfv7QbzMzv8Od1LV0FaGpVtduWh8OG" +
            "yufUoOCMgAd+tunhmTFmrQXyicSnHpjFlrZPIyrK//mutPaTVwPie37J0HWy8p87eUKlM0CjeDUb/ZHJfjiVgc2jKVjitcG328tI" +
            "Ich43uK2wv2XVsKuSR7WYsm6zsnCO1ja/iAqkcBqTiXgzgDBFeI/4WUrmAnS+Jn2LVkFaEZ73Y8VGnNrmwZbuMXQ9c0fwKbDUdgb" +
            "4GcCJTzf2OiCh79SDTfgOYLRYISXRwfMcPSMB1gC5dLuJb27ZAaQ/XjhtNibwIxvX0SErTt2wb8+0m38ttOfmTGEBSGzglhh26FJ" +
            "TNDJz4CJAjU4R2h2u8Ul7c/e6PPLXW4LfP8CD1TYKFhTw8EPF3uL9/SQ1DgWN984OAljKfmtVwIKCbNntaFxvmjWunh3q+2vW9y2" +
            "p1Y1+6DR5zBfVmgI8X48xsOuE0EYTcm/wtLYfyRVo7qzF84hmvX+gPej6r4ySh8IxjKLMN1VS/bs8Bh3kzz8EIafe0ei8PF4LLkn" +
            "LD3wiwl5C9ZJSRqESEDJf9g0nzQX1rj5ugrLje1OermPpTrxjUxKhYlxXunbOa0cjKpG+onkFY7Dp8DR3yXNlTsiOtCFB6mZE6ki" +
            "q02MHUZIRgJiPqz+nNDcIrIvIf3/3w7DeU7nPQP+DwAA//81pOBsAAAABklEQVQDAHURfxfcExJ5AAAAAElFTkSuQmCC";

        /// <summary>Новая текстура значка (64x64, RGBA, без мипмапов).</summary>
        public static Texture2D Create()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "SalamanderIcon",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideInHierarchy,
            };
            texture.LoadImage(System.Convert.FromBase64String(PngBase64));
            return texture;
        }
    }
}
