// see: http://habrahabr.ru/post/202162/

using System;
using System.Linq;
using System.Linq.Expressions;

namespace xops
{
    class Program
    {
        class BitExpr
        {
            public bool Is(bool value)
            {
                var ce = this as ConstExpr;
                return ce != null && ce.Const == value;
            }
        }

        class ParamExpr : BitExpr
        {
            public int BitId;
            public override string ToString() { return "b" + BitId; }
        }

        class ConstExpr : BitExpr
        {
            public bool Const;
            public override string ToString() { return Const ? "1" : "0"; }
        }

        class UnaryExpr : BitExpr
        {
            public string Op;
            public BitExpr Arg;
            public override string ToString() { return Op + "(" + Arg + ")"; }
        }

        class BinaryExpr : BitExpr
        {
            public string Op;
            public BitExpr Left;
            public BitExpr Right;
            public override string ToString() { return "(" + Left + ") " + Op + " (" + Right + ")"; }
        }

        static void Main(string[] args)
        {
            var exprs = new Expression<Func<int, bool>>[]{
                x => ~x != x,
                x => (x + -x & 1) == 0,
                x => ~x != x * 4u >> 2,
                x => (-x & 1) == (x & 1),
                x => ((-x ^ x) & 1) == 0,
                x => (x * 0x80 & 0x56) == 0,
                x => ~(-x * 0x40) != x << 6,
                x => (~(x * 0x80) & 0x3d) == 0x3d,
            };
            foreach (var e in exprs)
                CheckExpr(e);
        }

        private static void CheckExpr(Expression<Func<int, bool>> expr)
        {
            var body = (BinaryExpression)expr.Body;
            var l = Simplify(Traverse(body.Left));
            var r = Simplify(Traverse(body.Right));
            var result = CheckEq(l, r, body.NodeType == ExpressionType.Equal);
            Console.WriteLine("{0, -50}: is always {1}", expr.Body.ToString(), result);
        }

        private static bool CheckEq(BitExpr[] l, BitExpr[] r, bool equals)
        {
            for (var i = 0; i < l.Length; i++) {
                if ((l[i].ToString() == r[i].ToString()) ^ equals)
                    return false;
            }
            return true;
        }

        private static BitExpr[] Simplify(BitExpr[] bitExpr)
        {
            return bitExpr.Select(b => SimplifyOne(b)).ToArray();
        }

        private static BitExpr SimplifyOne(BitExpr expr)
        {
            if (expr is BinaryExpr) {
                var be = (BinaryExpr)expr;
                var l = be.Left = SimplifyOne(be.Left);
                var r = be.Right = SimplifyOne(be.Right);
                if (be.Op == "&") {
                    if (l.Is(false) || r.Is(false))
                        return new ConstExpr();
                    if (l.Is(true))
                        return r;
                    if (r.Is(true))
                        return l;
                }
                if (be.Op == "^") {
                    if (l.Is(false) || l.Is(true))
                        return l.Is(false) ? r : SimplifyOne(new UnaryExpr { Op = "~", Arg = r });
                    if (r.Is(false) || r.Is(true))
                        return r.Is(false) ? l : SimplifyOne(new UnaryExpr { Op = "~", Arg = l });
                    if (l.ToString() == r.ToString())
                        return new ConstExpr();
                }
            } else if (expr is UnaryExpr) {
                var ue = (UnaryExpr)expr;
                var a = ue.Arg = SimplifyOne(ue.Arg);
                if (ue.Op == "~") {
                    var aue = ue.Arg as UnaryExpr;
                    if (aue != null && aue.Op == "~")
                        return aue.Arg;
                }
            }
            return expr;
        }

        private static BitExpr[] Traverse(Expression expr)
        {
            switch (expr.NodeType) {
                case ExpressionType.Convert: {
                        return Traverse(((UnaryExpression)expr).Operand);
                    }
                case ExpressionType.And: {
                        var l = Traverse(((BinaryExpression)expr).Left);
                        var r = Traverse(((BinaryExpression)expr).Right);
                        return l.Zip(r, (a, b) => new BinaryExpr { Op = "&", Left = a, Right = b }).ToArray();
                    }
                case ExpressionType.ExclusiveOr: {
                        var l = Traverse(((BinaryExpression)expr).Left);
                        var r = Traverse(((BinaryExpression)expr).Right);
                        return l.Zip(r, (a, b) => new BinaryExpr { Op = "^", Left = a, Right = b }).ToArray();
                    }
                case ExpressionType.Not: {
                        var arg = Traverse(((UnaryExpression)expr).Operand);
                        return arg.Select(a => new UnaryExpr { Op = "~", Arg = a }).ToArray();
                    }
                case ExpressionType.Negate: {
                        var arg = Traverse(((UnaryExpression)expr).Operand);
                        var negated = arg.Select(a => new UnaryExpr { Op = "~", Arg = a }).ToArray();
                        var one = Enumerable.Repeat(new ConstExpr(), 32).ToArray();
                        one[0] = new ConstExpr { Const = true };
                        return Add(negated, one);
                    }
                case ExpressionType.Add: {
                        var l = Traverse(((BinaryExpression)expr).Left);
                        var r = Traverse(((BinaryExpression)expr).Right);
                        return Add(l, r);
                    }
                case ExpressionType.Multiply: {
                        var l = Traverse(((BinaryExpression)expr).Left);
                        var r = ((BinaryExpression)expr).Right;
                        if (r.NodeType == ExpressionType.Constant) {
                            var mult = Convert.ToInt32(((ConstantExpression)r).Value);
                            for (var i = 0; i < 32; i++) {
                                var bit = (1 << i) & mult;
                                if (bit != 0) {
                                    if (mult - bit == 0) {
                                        return Shift(l, i);
                                    } else {
                                        throw new NotImplementedException();
                                    }
                                }
                            }
                            throw new NotImplementedException();
                        } else {
                            throw new NotImplementedException();
                        }
                        //return l.Zip(r, (a, b) => new BinaryExpr { Op = "&", Left = a, Right = b }).ToArray();
                    }
                case ExpressionType.Parameter: {
                        return Enumerable.Range(0, 32).Select(i => new ParamExpr { BitId = i }).ToArray();
                    }
                case ExpressionType.Constant: {
                        var val = (int)((ConstantExpression)expr).Value;
                        return Enumerable.Range(0, 32).Select(i => new ConstExpr { Const = ((1 << i) & val) != 0 }).ToArray();
                    }
                case ExpressionType.RightShift: {
                        var l = Traverse(((BinaryExpression)expr).Left);
                        var r = ((BinaryExpression)expr).Right;
                        if (r.NodeType == ExpressionType.Constant) {
                            var shift = Convert.ToInt32(((ConstantExpression)r).Value);
                            return Shift(l, -shift);
                        } else {
                            throw new NotImplementedException();
                        }
                    }
                case ExpressionType.LeftShift: {
                        var l = Traverse(((BinaryExpression)expr).Left);
                        var r = ((BinaryExpression)expr).Right;
                        if (r.NodeType == ExpressionType.Constant) {
                            var shift = Convert.ToInt32(((ConstantExpression)r).Value);
                            return Shift(l, shift);
                        } else {
                            throw new NotImplementedException();
                        }
                    }
                default:
                    throw new NotImplementedException();
            }
        }

        private static BitExpr[] Add(BitExpr[] l, BitExpr[] r)
        {
            var result = new BitExpr[32];
            BitExpr carry = new ConstExpr();
            for (var i = 0; i < 32; i++) {
                var xored = new BinaryExpr { Op = "^", Left = l[i], Right = r[i] };
                var anded = new BinaryExpr { Op = "^", Left = l[i], Right = r[i] };
                if (carry.Is(false)) {
                    result[i] = xored;
                    carry = anded;
                } else {
                    var carryPrev = carry;
                    carry = new BinaryExpr {
                        Op = "|",
                        Left = new BinaryExpr { Op = "&", Left = carryPrev, Right = xored },
                        Right = anded,
                    };
                    result[i] = new BinaryExpr { Op = "^", Left = carryPrev, Right = xored };
                }
            }
            return result;
        }

        private static BitExpr[] Shift(BitExpr[] val, int i)
        {
            if (i >= 0) {
                // left shift
                return Enumerable.Repeat(new ConstExpr(), i).Concat(val).Take(32).ToArray();
            } else {
                // right shift
                i = -i;
                return val.Skip(i).Concat(Enumerable.Repeat(new ConstExpr(), i)).Take(32).ToArray();
            }
        }
    }
}
