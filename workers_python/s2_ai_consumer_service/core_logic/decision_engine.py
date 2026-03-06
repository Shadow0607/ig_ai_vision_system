import logging
from qdrant_client import QdrantClient
from qdrant_client.http import models

logger = logging.getLogger(__name__)

class DecisionEngine:
    @classmethod
    def compare_face_logic(cls, target_embedding, system_name: str, qdrant_client: QdrantClient, db_threshold: float):
        """🚀 純向量資料庫檢索決策 (Qdrant Query Points)"""
        
        # ==========================================
        # 1. 向 Qdrant 查詢最相似的「正樣本 (pos)」
        # ==========================================
        pos_response = qdrant_client.query_points( # 🌟 修正1：改用 query_points
            collection_name="ig_faces",
            query=target_embedding,              # 🌟 修正2：參數改為 query
            query_filter=models.Filter(
                must=[
                    models.FieldCondition(key="system_name", match=models.MatchValue(value=system_name)),
                    models.FieldCondition(key="label", match=models.MatchValue(value="pos"))
                ]
            ),
            limit=1
        )
        
        pos_search = pos_response.points # 🌟 修正3：必須從 response.points 取出陣列
        
        if not pos_search:
            return "INITIAL_REVIEW", 0.0, "INITIAL_REVIEW"
            
        curr_sim = pos_search[0].score 
        
        # ==========================================
        # 2. 向 Qdrant 查詢「負樣本 (neg)」(包含個人與全域)
        # ==========================================
        neg_response = qdrant_client.query_points( # 🌟 同步修改
            collection_name="ig_faces",
            query=target_embedding,
            query_filter=models.Filter(
                should=[
                    models.Filter(must=[
                        models.FieldCondition(key="system_name", match=models.MatchValue(value=system_name)),
                        models.FieldCondition(key="label", match=models.MatchValue(value="neg"))
                    ]),
                    models.Filter(must=[
                        models.FieldCondition(key="label", match=models.MatchValue(value="global_neg"))
                    ])
                ]
            ),
            limit=1
        )
        
        neg_search = neg_response.points # 🌟 同步修改
        max_neg_sim = neg_search[0].score if neg_search else -1.0
        
        logger.info(f"🔮 Qdrant 分析 | 正向最高: {curr_sim:.4f} | 負向最高: {max_neg_sim:.4f}")

        # 🛑 絕對防護網：與垃圾特徵更像，直接排除
        if curr_sim <= max_neg_sim:
            logger.warning(f"🛑 觸發防護網：與負樣本更相似 ({max_neg_sim:.4f} >= {curr_sim:.4f})，直接排除。")
            return "GARBAGE", curr_sim, "SKIP" 

        # ==========================================
        # 3. 單純而強大的分數判定
        # ==========================================
        final_score = curr_sim
        HITL_MARGIN = 0.10
        hitl_line = db_threshold - HITL_MARGIN
        
        if final_score >= db_threshold:
            return "MATCH_VSTACK", final_score, "OUTPUT" 
        elif hitl_line <= final_score < db_threshold:
            return "PENDING", final_score, "PENDING"
        else:
            return "GARBAGE", final_score, "SKIP"