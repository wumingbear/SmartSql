﻿using Microsoft.Extensions.Logging;
using SmartSql.Abstractions;
using SmartSql.Configuration.Statements;
using SmartSql.Configuration.Tags;
using SmartSql.Exceptions;
using SmartSql.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SmartSql.DyRepository
{
    public class RepositoryBuilder : IRepositoryBuilder
    {
        private ScopeTemplateParser _templateParser;
        private AssemblyBuilder _assemblyBuilder;
        private ModuleBuilder _moduleBuilder;
        SqlCommandAnalyzer _commandAnalyzer;
        public RepositoryBuilder(
             string scope_template
            , ILogger<RepositoryBuilder> logger
            )
        {
            _templateParser = new ScopeTemplateParser(scope_template);
            _commandAnalyzer = new SqlCommandAnalyzer();
            InitAssembly();
            _logger = logger;
        }

        private void InitAssembly()
        {
            string assemblyName = "SmartSql.RepositoryImpl" + this.GetHashCode();
            _assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName
            {
                Name = assemblyName
            }, AssemblyBuilderAccess.Run);
            _moduleBuilder = _assemblyBuilder.DefineDynamicModule(assemblyName + ".dll");
        }

        /// <summary>
        /// 构建仓储接口实现
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public Type BuildRepositoryImpl(Type interfaceType, ISmartSqlMapper smartSqlMapper, string scope = "")
        {
            string implName = $"{interfaceType.Name.TrimStart('I')}_Impl_{Guid.NewGuid().ToString("N")}";
            var typeBuilder = _moduleBuilder.DefineType(implName, TypeAttributes.Public);
            typeBuilder.AddInterfaceImplementation(interfaceType);
            var sqlMapperField = typeBuilder.DefineField("sqlMapper", typeof(ISmartSqlMapper), FieldAttributes.Family);
            var scopeField = typeBuilder.DefineField("scope", typeof(string), FieldAttributes.Family);
            scope = PreScoe(interfaceType, scope);
            EmitBuildCtor(scope, typeBuilder, sqlMapperField, scopeField);
            var interfaceMethods = new List<MethodInfo>();

            var currentMethodInfos = interfaceType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            interfaceMethods.AddRange(currentMethodInfos);
            var currentIIs = interfaceType.GetInterfaces();
            foreach (var currentII in currentIIs)
            {
                var currentIIMethods = currentII.GetMethods(BindingFlags.Instance | BindingFlags.Public);
                interfaceMethods.AddRange(currentIIMethods);
            }
            foreach (var methodInfo in interfaceMethods)
            {
                BuildMethod(typeBuilder, methodInfo, sqlMapperField, smartSqlMapper, scope);
            }
            return typeBuilder.CreateTypeInfo();
        }
        private void EmitBuildCtor(string scope, TypeBuilder typeBuilder, FieldBuilder sqlMapperField, FieldBuilder scopeField)
        {
            var paramTypes = new Type[] { typeof(ISmartSqlMapper) };
            var ctorBuilder = typeBuilder.DefineConstructor(
                MethodAttributes.Public, CallingConventions.Standard, paramTypes);
            var ctorIL = ctorBuilder.GetILGenerator();

            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Call, (typeof(object).GetConstructor(Type.EmptyTypes)));
            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Ldarg_1);
            ctorIL.Emit(OpCodes.Stfld, sqlMapperField);
            ctorIL.Emit(OpCodes.Ldarg_0);
            ctorIL.Emit(OpCodes.Ldstr, scope);
            ctorIL.Emit(OpCodes.Stfld, scopeField);
            ctorIL.Emit(OpCodes.Ret);
        }

        private string PreScoe(Type interfaceType, string scope = "")
        {
            var sqlmapAttr = interfaceType.GetCustomAttribute<SqlMapAttribute>();
            if (sqlmapAttr != null && !String.IsNullOrEmpty(sqlmapAttr.Scope))
            {
                scope = sqlmapAttr.Scope;
            }
            else if (String.IsNullOrEmpty(scope))
            {
                scope = _templateParser.Parse(interfaceType.Name);
            }
            return scope;
        }
        private readonly static Type _reqContextType = typeof(RequestContext);
        private readonly static Type _taskType = typeof(Task);
        private readonly static Type _voidType = typeof(void);
        private readonly static Type _dbParametersType = typeof(DbParameterCollection);
        private readonly static Type _enumerableType = typeof(IEnumerable);

        private readonly static ConstructorInfo _reqContextCtor = _reqContextType.GetConstructor(Type.EmptyTypes);
        private readonly static MethodInfo _set_DataSourceChoiceMethod = _reqContextType.GetMethod("set_DataSourceChoice");
        private readonly static MethodInfo _set_CommandTypeMethod = _reqContextType.GetMethod("set_CommandType");
        private readonly static MethodInfo _set_ScopeMethod = _reqContextType.GetMethod("set_Scope");
        private readonly static MethodInfo _set_SqlIdMethod = _reqContextType.GetMethod("set_SqlId");
        private readonly static MethodInfo _set_RequestMethod = _reqContextType.GetMethod("set_Request");
        private readonly static MethodInfo _set_RealSqlMethod = _reqContextType.GetMethod("set_RealSql");

        private readonly static ConstructorInfo _dbParametersCtor = _dbParametersType.GetConstructor(new Type[] { typeof(bool) });
        private readonly static MethodInfo _addDbParamMehtod = _dbParametersType.GetMethod("Add", new Type[] { typeof(string), typeof(object) });

        private readonly ILogger<RepositoryBuilder> _logger;

        private void BuildMethod(TypeBuilder typeBuilder, MethodInfo methodInfo, FieldBuilder sqlMapperField, ISmartSqlMapper smartSqlMapper, string scope)
        {
            var methodParams = methodInfo.GetParameters();
            var paramTypes = methodParams.Select(m => m.ParameterType).ToArray();
            if (paramTypes.Any(p => p.IsGenericParameter))
            {
                _logger.LogError("SmartSql.DyRepository method parameters do not support generic parameters for the time being!");
                throw new SmartSqlException("SmartSql.DyRepository method parameters do not support generic parameters for the time being!");
            }
            var returnType = methodInfo.ReturnType;

            var implMehtod = typeBuilder.DefineMethod(methodInfo.Name
                , MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final
                , returnType, paramTypes);

            var isTaskReturnType = _taskType.IsAssignableFrom(returnType);

            if (methodInfo.IsGenericMethod)
            {
                var genericArgs = methodInfo.GetGenericArguments();
                var gArgNames = genericArgs.Select(gArg => gArg.Name).ToArray();
                var defineGenericArgs = implMehtod.DefineGenericParameters(gArgNames);
                for (int i = 0; i < gArgNames.Length; i++)
                {
                    var genericArg = genericArgs[i];
                    var defineGenericArg = defineGenericArgs[i];
                    defineGenericArg.SetGenericParameterAttributes(genericArg.GenericParameterAttributes);
                }
            }

            StatementAttribute statementAttr = PreStatement(scope, methodInfo, returnType, isTaskReturnType, smartSqlMapper);
            var ilGenerator = implMehtod.GetILGenerator();
            ilGenerator.DeclareLocal(_reqContextType);
            ilGenerator.DeclareLocal(_dbParametersType);
            if (IsValueTuple(returnType))
            {
                ilGenerator.DeclareLocal(_multipleResultType);
                ilGenerator.DeclareLocal(returnType);
            }
            if (paramTypes.Length == 1 && paramTypes.First() == _reqContextType)
            {
                ilGenerator.Emit(OpCodes.Ldarg_1);
                ilGenerator.Emit(OpCodes.Stloc_0);
            }
            else
            {
                EmitNewRequestContext(ilGenerator);
                SetCmdTypeAndSourceChoice(statementAttr, ilGenerator);

                if (String.IsNullOrEmpty(statementAttr.Sql))
                {
                    EmitSetScope(ilGenerator, statementAttr.Scope);
                    EmitSetSqlId(ilGenerator, statementAttr);
                }
                else
                {
                    EmitSetRealSql(ilGenerator, statementAttr);
                }
                if (paramTypes.Length == 1 && !IsSimpleParam(paramTypes.First()))
                {
                    ilGenerator.Emit(OpCodes.Ldloc_0);
                    ilGenerator.Emit(OpCodes.Ldarg_1);
                    ilGenerator.Emit(OpCodes.Call, _set_RequestMethod);
                }
                else if (paramTypes.Length > 0)
                {
                    bool ignoreParameterCase = smartSqlMapper.SmartSqlOptions.SmartSqlContext.IgnoreParameterCase;
                    ilGenerator.Emit(ignoreParameterCase ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                    ilGenerator.Emit(OpCodes.Newobj, _dbParametersCtor);
                    ilGenerator.Emit(OpCodes.Stloc_1);
                    for (int i = 0; i < methodParams.Length; i++)
                    {
                        int argIndex = i + 1;
                        var reqParam = methodParams[i];
                        string reqParamName = reqParam.Name;
                        var paramAttr = reqParam.GetCustomAttribute<ParamAttribute>();
                        if (paramAttr != null && !String.IsNullOrEmpty(paramAttr.Name))
                        {
                            reqParamName = paramAttr.Name;
                        }
                        ilGenerator.Emit(OpCodes.Ldloc_1); //[dic]
                        ilGenerator.Emit(OpCodes.Ldstr, reqParamName);//[dic][param-name]
                        EmitUtils.LoadArg(ilGenerator, argIndex);
                        if (reqParam.ParameterType.IsValueType)
                        {
                            ilGenerator.Emit(OpCodes.Box, reqParam.ParameterType);
                        }
                        ilGenerator.Emit(OpCodes.Call, _addDbParamMehtod);//[empty]
                    }
                    ilGenerator.Emit(OpCodes.Ldloc_0);
                    ilGenerator.Emit(OpCodes.Ldloc_1);
                    ilGenerator.Emit(OpCodes.Call, _set_RequestMethod);
                }
            }

            MethodInfo executeMethod = null;
            executeMethod = PreExecuteMethod(statementAttr, returnType, isTaskReturnType);
            ilGenerator.Emit(OpCodes.Ldarg_0);// [this]
            ilGenerator.Emit(OpCodes.Ldfld, sqlMapperField);//[this][sqlMapper]
            ilGenerator.Emit(OpCodes.Ldloc_0);//[sqlMapper][requestContext]
            ilGenerator.Emit(OpCodes.Call, executeMethod);
            if (returnType == _voidType)
            {
                ilGenerator.Emit(OpCodes.Pop);
            }
            if (IsValueTuple(returnType))
            {
                ilGenerator.Emit(OpCodes.Stloc_2);
                QueryMultipleToValueTuple(methodInfo, returnType, ilGenerator);
            }
            ilGenerator.Emit(OpCodes.Ret);

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug($"RepositoryBuilder.BuildMethod:{methodInfo.Name}->Statement:[Scope:{statementAttr.Scope},Id:{statementAttr.Id},Execute:{statementAttr.Execute},Sql:{statementAttr.Sql},IsAsync:{isTaskReturnType}]");
            }
        }
        private readonly static Type _disposableType = typeof(IDisposable);
        private readonly static Type _multipleResultType = typeof(IMultipleResult);
        private readonly static Type _valueTupleType = typeof(ValueTuple);
        private readonly static MethodInfo _multipleResult_Read = _multipleResultType.GetMethod("Read");
        private readonly static MethodInfo _multipleResult_ReadSingle = _multipleResultType.GetMethod("ReadSingle");
        private readonly static MethodInfo _dispose_Method = _disposableType.GetMethod("Dispose");
        private static void QueryMultipleToValueTuple(MethodInfo methodInfo, Type returnType, ILGenerator ilGenerator)
        {
            var returnGenericTypeArguments = returnType.GenericTypeArguments;
            if (returnGenericTypeArguments.Length > 8)
            {
                throw new SmartSqlException($"SmartSql.DyRepository method:{methodInfo.Name} return type ValueTuple More than 8!");
            }
            var createVT = _valueTupleType.GetMethods().First(m =>
            {
                if (m.Name != "Create") { return false; }
                return m.GetParameters().Length == returnGenericTypeArguments.Length;
            }).MakeGenericMethod(returnGenericTypeArguments);

            ilGenerator.BeginExceptionBlock();
            foreach (var typeArg in returnGenericTypeArguments)
            {
                ilGenerator.Emit(OpCodes.Ldloc_2);
                bool isEnum = _enumerableType.IsAssignableFrom(typeArg);
                var readMethod = isEnum ? _multipleResult_Read : _multipleResult_ReadSingle;
                var realRetType = isEnum ? typeArg.GenericTypeArguments[0] : typeArg;
                readMethod = readMethod.MakeGenericMethod(new Type[] { realRetType });
                ilGenerator.Emit(OpCodes.Call, readMethod);
            }
            ilGenerator.Emit(OpCodes.Call, createVT);
            ilGenerator.Emit(OpCodes.Stloc_3);
            ilGenerator.BeginFinallyBlock();
            ilGenerator.Emit(OpCodes.Ldloc_2);
            ilGenerator.Emit(OpCodes.Call, _dispose_Method);
            ilGenerator.Emit(OpCodes.Endfinally);
            ilGenerator.EndExceptionBlock();
            ilGenerator.Emit(OpCodes.Ldloc_3);
        }

        private static void SetCmdTypeAndSourceChoice(StatementAttribute statementAttr, ILGenerator ilGenerator)
        {
            if (statementAttr.CommandType != CommandType.Text)
            {
                ilGenerator.Emit(OpCodes.Ldloc_0);
                EmitUtils.LoadInt32(ilGenerator, statementAttr.CommandType.GetHashCode());
                ilGenerator.Emit(OpCodes.Call, _set_CommandTypeMethod);
            }
            if (statementAttr.SourceChoice != DataSourceChoice.Unknow)
            {
                ilGenerator.Emit(OpCodes.Ldloc_0);
                EmitUtils.LoadInt32(ilGenerator, statementAttr.SourceChoice.GetHashCode());
                ilGenerator.Emit(OpCodes.Call, _set_DataSourceChoiceMethod);
            }
        }

        private void EmitNewRequestContext(ILGenerator ilGenerator)
        {
            ilGenerator.Emit(OpCodes.Newobj, _reqContextCtor);
            ilGenerator.Emit(OpCodes.Stloc_0);
        }

        private void EmitSetRealSql(ILGenerator ilGenerator, StatementAttribute statementAttr)
        {
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Ldstr, statementAttr.Sql);
            ilGenerator.Emit(OpCodes.Call, _set_RealSqlMethod);
        }
        private void EmitSetSqlId(ILGenerator ilGenerator, StatementAttribute statementAttr)
        {
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Ldstr, statementAttr.Id);
            ilGenerator.Emit(OpCodes.Call, _set_SqlIdMethod);
        }

        private void EmitSetScope(ILGenerator ilGenerator, string scope)
        {
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Ldstr, scope);
            ilGenerator.Emit(OpCodes.Call, _set_ScopeMethod);
        }

        private StatementAttribute PreStatement(string scope, MethodInfo methodInfo, Type returnType, bool isTaskReturnType, ISmartSqlMapper smartSqlMapper)
        {
            returnType = isTaskReturnType ? returnType.GetGenericArguments().FirstOrDefault() : returnType;
            var statementAttr = methodInfo.GetCustomAttribute<StatementAttribute>();
            var methodName = methodInfo.Name;
            if (isTaskReturnType && methodInfo.Name.EndsWith("Async"))
            {
                methodName = methodName.Substring(0, methodName.Length - 5);
            }
            if (statementAttr != null)
            {
                statementAttr.Id = !String.IsNullOrEmpty(statementAttr.Id) ? statementAttr.Id : methodName;
                statementAttr.Scope = !String.IsNullOrEmpty(statementAttr.Scope) ? statementAttr.Scope : scope;
            }
            else
            {
                statementAttr = new StatementAttribute
                {
                    Scope = scope,
                    Id = methodName
                };
            }
            if (returnType == typeof(DataTable))
            {
                statementAttr.Execute = ExecuteBehavior.GetDataTable;
                return statementAttr;
            }
            if (returnType == typeof(DataSet))
            {
                statementAttr.Execute = ExecuteBehavior.GetDataSet;
                return statementAttr;
            }
            if (returnType == typeof(IMultipleResult))
            {
                statementAttr.Execute = ExecuteBehavior.QueryMultiple;
                return statementAttr;
            }
            if (IsValueTuple(returnType))
            {
                statementAttr.Execute = ExecuteBehavior.QueryMultiple;
                return statementAttr;
            }
            if (statementAttr.Execute == ExecuteBehavior.Auto)
            {
                SqlCommandType cmdType = SqlCommandType.Unknown;
                if (statementAttr.CommandType != CommandType.StoredProcedure)
                {
                    cmdType = AnalyseCmdType(smartSqlMapper, statementAttr);
                }
                if (returnType == typeof(int) || returnType == _voidType || returnType == null)
                {
                    statementAttr.Execute = ExecuteBehavior.Execute;
                    if (returnType == typeof(int))
                    {
                        if (cmdType.HasFlag(SqlCommandType.Select))
                        {
                            statementAttr.Execute = ExecuteBehavior.ExecuteScalar;
                        }
                    }
                }
                else if (returnType.IsValueType || returnType == typeof(string))
                {
                    statementAttr.Execute = ExecuteBehavior.ExecuteScalar;
                    if (!cmdType.HasFlag(SqlCommandType.Select))
                    {
                        statementAttr.Execute = ExecuteBehavior.Execute;
                    }
                }
                else
                {
                    var isQueryEnumerable = typeof(IEnumerable).IsAssignableFrom(returnType);
                    if (isQueryEnumerable)
                    {
                        statementAttr.Execute = ExecuteBehavior.Query;
                    }
                    else
                    {
                        statementAttr.Execute = ExecuteBehavior.QuerySingle;
                    }
                }
            }
            return statementAttr;
        }

        private SqlCommandType AnalyseCmdType(ISmartSqlMapper smartSqlMapper, StatementAttribute statementAttr)
        {
            var realSqlStr = statementAttr.Sql;
            if (String.IsNullOrEmpty(statementAttr.Sql))
            {
                realSqlStr = _commandAnalyzer.BuildStatementFullSql(smartSqlMapper, statementAttr.Scope, statementAttr.Id);
            }
            var cmdType = _commandAnalyzer.Analyse(realSqlStr);
            return cmdType;
        }

        private MethodInfo PreExecuteMethod(StatementAttribute statementAttr, Type returnType, bool isTaskReturnType)
        {
            MethodInfo executeMethod;
            if (isTaskReturnType)
            {
                var realReturnType = returnType.GenericTypeArguments.FirstOrDefault();
                switch (statementAttr.Execute)
                {
                    case ExecuteBehavior.Execute:
                        {
                            executeMethod = typeof(ISmartSqlMapperAsync).GetMethod("ExecuteAsync", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    case ExecuteBehavior.ExecuteScalar:
                        {
                            var method = typeof(ISmartSqlMapperAsync).GetMethod("ExecuteScalarAsync", new Type[] { typeof(RequestContext) });

                            executeMethod = method.MakeGenericMethod(new Type[] { realReturnType });
                            break;
                        }
                    case ExecuteBehavior.QuerySingle:
                        {
                            var method = typeof(ISmartSqlMapperAsync).GetMethod("QuerySingleAsync", new Type[] { typeof(RequestContext) });
                            executeMethod = method.MakeGenericMethod(new Type[] { realReturnType });
                            break;
                        }
                    case ExecuteBehavior.Query:
                        {
                            var method = typeof(ISmartSqlMapperAsync).GetMethod("QueryAsync", new Type[] { typeof(RequestContext) });
                            var enumerableType = realReturnType.GenericTypeArguments[0];
                            executeMethod = method.MakeGenericMethod(new Type[] { enumerableType });
                            break;
                        }
                    case ExecuteBehavior.GetDataTable:
                        {
                            executeMethod = typeof(ISmartSqlMapperAsync).GetMethod("GetDataTableAsync", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    case ExecuteBehavior.GetDataSet:
                        {
                            executeMethod = typeof(ISmartSqlMapperAsync).GetMethod("GetDataSetAsync", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    case ExecuteBehavior.QueryMultiple:
                        {
                            executeMethod = typeof(ISmartSqlMapper).GetMethod("QueryMultipleAsync", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    default: { throw new ArgumentException(); }
                }
            }
            else
            {
                switch (statementAttr.Execute)
                {
                    case ExecuteBehavior.Execute:
                        {
                            executeMethod = typeof(ISmartSqlMapper).GetMethod("Execute", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    case ExecuteBehavior.ExecuteScalar:
                        {
                            var method = typeof(ISmartSqlMapper).GetMethod("ExecuteScalar", new Type[] { typeof(RequestContext) });
                            executeMethod = method.MakeGenericMethod(new Type[] { returnType });
                            break;
                        }
                    case ExecuteBehavior.QuerySingle:
                        {
                            var method = typeof(ISmartSqlMapper).GetMethod("QuerySingle", new Type[] { typeof(RequestContext) });
                            executeMethod = method.MakeGenericMethod(new Type[] { returnType });
                            break;
                        }
                    case ExecuteBehavior.Query:
                        {
                            var method = typeof(ISmartSqlMapper).GetMethod("Query", new Type[] { typeof(RequestContext) });
                            var enumerableType = returnType.GenericTypeArguments[0];
                            executeMethod = method.MakeGenericMethod(new Type[] { enumerableType });
                            break;
                        }
                    case ExecuteBehavior.GetDataTable:
                        {
                            executeMethod = typeof(ISmartSqlMapper).GetMethod("GetDataTable", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    case ExecuteBehavior.GetDataSet:
                        {
                            executeMethod = typeof(ISmartSqlMapper).GetMethod("GetDataSet", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    case ExecuteBehavior.QueryMultiple:
                        {
                            executeMethod = typeof(ISmartSqlMapper).GetMethod("QueryMultiple", new Type[] { typeof(RequestContext) });
                            break;
                        }
                    default: { throw new ArgumentException(); }
                }
            }

            return executeMethod;
        }
        private bool IsValueTuple(Type type)
        {
            return type != null && type.ToString().StartsWith("System.ValueTuple");
        }
        private bool IsSimpleParam(Type paramType)
        {
            if (paramType == typeof(CommandType))
            {
                return false;
            }
            if (paramType == typeof(DataSourceChoice))
            {
                return false;
            }
            if (paramType.IsValueType) { return true; }
            if (paramType == typeof(string)) { return true; }
            if (paramType.IsGenericParameter) { return true; }
            return _enumerableType.IsAssignableFrom(paramType);
        }
    }
}
