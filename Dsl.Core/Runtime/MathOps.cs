using System;
using System.Globalization;
using Dsl.Codegen;

namespace Dsl.Runtime
{
    /// <summary>
    /// Встроенный Math: чистые функции без состояния, поэтому ни сейв, ни
    /// отпечаток программы о них ничего не знают — в байткоде это обычный
    /// CallEngine. Тип операции выбирает чекер: аргументы уже приведены к общему
    /// числовому типу (int/float/double), и здесь ветвление идёт по тегу первого.
    ///
    /// Политика ошибок та же, что у остального языка: никаких тихих NaN,
    /// бесконечностей и переполнений — вместо них ScriptError с понятным текстом,
    /// которая убивает только этот файбер.
    /// </summary>
    internal static class MathOps
    {
        public static Variant Exec(EngineOp op, Variant[] stack, int argBase)
        {
            Variant A(int i) => stack[argBase + i];

            switch (op)
            {
                case EngineOp.MathMin:
                case EngineOp.MathMax:
                {
                    var a = A(0);
                    var b = A(1);
                    bool min = op == EngineOp.MathMin;
                    switch (a.Type)
                    {
                        case VariantType.Int:
                            return Variant.Int(min ? Math.Min(a.AsInt, b.AsInt) : Math.Max(a.AsInt, b.AsInt));
                        case VariantType.Double:
                            return Variant.Double(min ? Math.Min(a.AsDouble, b.AsDouble) : Math.Max(a.AsDouble, b.AsDouble));
                        default:
                            return Variant.Float(min ? Math.Min(a.AsFloat, b.AsFloat) : Math.Max(a.AsFloat, b.AsFloat));
                    }
                }

                case EngineOp.MathClamp:
                {
                    var x = A(0);
                    var lo = A(1);
                    var hi = A(2);
                    switch (x.Type)
                    {
                        case VariantType.Int:
                        {
                            int l = lo.AsInt, h = hi.AsInt;
                            if (l > h) throw ClampOrder(Str(lo), Str(hi));
                            return Variant.Int(Math.Max(l, Math.Min(h, x.AsInt)));
                        }
                        case VariantType.Double:
                        {
                            double l = lo.AsDouble, h = hi.AsDouble;
                            if (l > h) throw ClampOrder(Str(lo), Str(hi));
                            return Variant.Double(Math.Max(l, Math.Min(h, x.AsDouble)));
                        }
                        default:
                        {
                            float l = lo.AsFloat, h = hi.AsFloat;
                            if (l > h) throw ClampOrder(Str(lo), Str(hi));
                            return Variant.Float(Math.Max(l, Math.Min(h, x.AsFloat)));
                        }
                    }
                }

                case EngineOp.MathAbs:
                {
                    var x = A(0);
                    switch (x.Type)
                    {
                        case VariantType.Int:
                            // |int.MinValue| в int не помещается: Math.Abs бросил бы
                            // OverflowException, и отчёт вышел бы «внутренней ошибкой»
                            if (x.AsInt == int.MinValue)
                                throw new ScriptError("Math.Abs: модуль -2147483648 не помещается в int.");
                            return Variant.Int(Math.Abs(x.AsInt));
                        case VariantType.Double:
                            return Variant.Double(Math.Abs(x.AsDouble));
                        default:
                            return Variant.Float(Math.Abs(x.AsFloat));
                    }
                }

                case EngineOp.MathSign:
                {
                    var x = A(0);
                    switch (x.Type)
                    {
                        case VariantType.Int: return Variant.Int(Math.Sign(x.AsInt));
                        case VariantType.Double: return Variant.Int(SignOf(x.AsDouble));
                        default: return Variant.Int(SignOf(x.AsFloat));
                    }
                }

                case EngineOp.MathFloor:
                case EngineOp.MathCeil:
                case EngineOp.MathRound:
                {
                    var x = A(0);
                    if (x.Type == VariantType.Int) return x; // целое округлять некуда
                    double d = x.Type == VariantType.Double ? x.AsDouble : x.AsFloat;
                    double r = op == EngineOp.MathFloor ? Math.Floor(d)
                             : op == EngineOp.MathCeil ? Math.Ceiling(d)
                             // от нуля, а не банковское: дизайнер ждёт, что 2.5 → 3
                             : Math.Round(d, MidpointRounding.AwayFromZero);
                    if (double.IsNaN(r) || r < int.MinValue || r > int.MaxValue)
                        throw new ScriptError(
                            $"Math.{Name(op)}: значение {Str(x)} не помещается в int.");
                    return Variant.Int((int)r);
                }

                case EngineOp.MathSqrt:
                {
                    var x = A(0);
                    double d = x.Type == VariantType.Double ? x.AsDouble : x.AsFloat;
                    if (d < 0 || double.IsNaN(d))
                        throw new ScriptError($"Math.Sqrt: корень из отрицательного числа {Str(x)}.");
                    return Result(x.Type, Math.Sqrt(d), "Sqrt");
                }

                case EngineOp.MathPow:
                {
                    var b = A(0);
                    var e = A(1);
                    double r = b.Type == VariantType.Double
                        ? Math.Pow(b.AsDouble, e.AsDouble)
                        : Math.Pow(b.AsFloat, e.AsFloat);
                    return Result(b.Type, r, "Pow");
                }

                case EngineOp.MathLerp:
                {
                    // t зажат в [0, 1], как у Mathf.Lerp: Lerp(a, b, 2) — это b, а не 2b - a
                    var a = A(0);
                    var b = A(1);
                    var t = A(2);
                    if (a.Type == VariantType.Double)
                    {
                        double tt = Clamp01(t.AsDouble);
                        return Variant.Double(a.AsDouble + (b.AsDouble - a.AsDouble) * tt);
                    }
                    float tf = (float)Clamp01(t.AsFloat);
                    return Variant.Float(a.AsFloat + (b.AsFloat - a.AsFloat) * tf);
                }

                default:
                    throw new ScriptError($"Неизвестная операция Math: {op}.");
            }
        }

        /// <summary>float или double по типу аргумента; бесконечность и NaN не выпускаем наружу.</summary>
        private static Variant Result(VariantType type, double r, string name)
        {
            if (type == VariantType.Double)
            {
                if (double.IsNaN(r) || double.IsInfinity(r))
                    throw new ScriptError($"Math.{name}: результат не является конечным числом.");
                return Variant.Double(r);
            }
            float f = (float)r;
            if (float.IsNaN(f) || float.IsInfinity(f))
                throw new ScriptError($"Math.{name}: результат не является конечным числом (или не помещается во float).");
            return Variant.Float(f);
        }

        private static int SignOf(double d) => d > 0 ? 1 : d < 0 ? -1 : 0; // NaN → 0, без исключения Math.Sign

        private static double Clamp01(double t) => t < 0 ? 0 : t > 1 ? 1 : t; // NaN проходит как есть — как и в арифметике

        private static ScriptError ClampOrder(string lo, string hi) =>
            new ScriptError($"Math.Clamp: нижняя граница {lo} больше верхней {hi}.");

        private static string Name(EngineOp op) =>
            op == EngineOp.MathFloor ? "Floor" : op == EngineOp.MathCeil ? "Ceil" : "Round";

        private static string Str(Variant v)
        {
            switch (v.Type)
            {
                case VariantType.Int: return v.AsInt.ToString(CultureInfo.InvariantCulture);
                case VariantType.Double: return v.AsDouble.ToString("R", CultureInfo.InvariantCulture);
                default: return v.AsFloat.ToString("R", CultureInfo.InvariantCulture);
            }
        }
    }
}
